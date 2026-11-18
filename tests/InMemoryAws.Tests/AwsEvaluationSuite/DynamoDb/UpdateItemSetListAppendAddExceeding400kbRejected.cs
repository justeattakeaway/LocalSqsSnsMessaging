// Translated from behavioral/dynamodb/check_update_item_set_list_append_add_exceeding_400kb_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.update-item-set-list-append-add-exceeding-400kb-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class UpdateItemSetListAppendAddExceeding400kbRejected
{
    private const string Check = "update-item-set-list-append-add-exceeding-400kb-rejected";
    private const string ErrValidation = "ValidationException";
    private const string MsgOverLimit = "Item size to update has exceeded the maximum allowed size";
    private const int KB = 1024;

    /// <summary>
    /// An UpdateItem that would grow the stored item past 400 KB is rejected with
    /// ValidationException whichever clause does the growing (SET, list_append, ADD to a
    /// set), and the stored item is left exactly as it was before the attempt.
    ///
    /// Reference: https://docs.aws.amazon.com/amazondynamodb/latest/developerguide/ServiceQuotas.html#limits-items
    /// </summary>
    [Test]
    [DisplayName("dynamodb.update-item-set-list-append-add-exceeding-400kb-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-growth-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real", Value = Check }],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(table));
        await ddb.WaitForTableExists(table, delaySeconds: 2, maxAttempts: 30);

        var oversized = new string('x', 401 * KB);

        // Positive control: an ordinary UpdateItem SET on this table succeeds and reads back,
        // so a target that rejects every UpdateItem (not just oversized ones) cannot pass here.
        await ddb.PutItemAsync(table, Item(("pk", S("control"))));
        await ddb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = table,
            Key = Item(("pk", S("control"))),
            UpdateExpression = "SET note = :v",
            ExpressionAttributeValues = Item((":v", S("ok"))),
        });
        var control = await Get(ddb, table, "control");
        Ensure(
            control.TryGetValue("note", out var note) && note.S == "ok",
            $"positive control UpdateItem/GetItem round-trip failed, got {Py.Repr(control)}");

        // SET of a large value.
        await ddb.PutItemAsync(table, Item(("pk", S("via-set"))));
        var (code, message) = await CodeAndMessage(() => ddb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = table,
            Key = Item(("pk", S("via-set"))),
            UpdateExpression = "SET bigattr = :v",
            ExpressionAttributeValues = Item((":v", S(oversized))),
        }));
        Ensure(code == ErrValidation, $"SET of a 401KB value must be {ErrValidation}, got {Py.Repr(code)}");
        Ensure(message == MsgOverLimit, $"expected AWS's exact message, got {Py.Repr(message)}");
        var afterSet = await Get(ddb, table, "via-set");
        Ensure(!afterSet.ContainsKey("bigattr"), $"a rejected SET must leave no trace, item is {Py.Repr(afterSet)}");

        // list_append onto an existing list.
        await ddb.PutItemAsync(table, Item(("pk", S("via-list-append")), ("biglist", L())));
        (code, message) = await CodeAndMessage(() => ddb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = table,
            Key = Item(("pk", S("via-list-append"))),
            UpdateExpression = "SET biglist = list_append(biglist, :v)",
            ExpressionAttributeValues = Item((":v", L(S(oversized)))),
        }));
        Ensure(code == ErrValidation, $"list_append growing past 400KB must be {ErrValidation}, got {Py.Repr(code)}");
        Ensure(message == MsgOverLimit, $"expected AWS's exact message, got {Py.Repr(message)}");
        var afterList = await Get(ddb, table, "via-list-append");
        Ensure(
            afterList.TryGetValue("biglist", out var biglist) && biglist.L is { Count: 0 },
            $"a rejected list_append must leave the list exactly as it was, got {Py.Repr(afterList)}");

        // ADD onto an existing string set.
        await ddb.PutItemAsync(table, Item(("pk", S("via-add")), ("bigset", SS("seed"))));
        (code, message) = await CodeAndMessage(() => ddb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = table,
            Key = Item(("pk", S("via-add"))),
            UpdateExpression = "ADD bigset :v",
            ExpressionAttributeValues = Item((":v", SS(oversized))),
        }));
        Ensure(code == ErrValidation, $"ADD growing a set past 400KB must be {ErrValidation}, got {Py.Repr(code)}");
        Ensure(message == MsgOverLimit, $"expected AWS's exact message, got {Py.Repr(message)}");
        var afterAdd = await Get(ddb, table, "via-add");
        Ensure(
            afterAdd.TryGetValue("bigset", out var bigset) && bigset.SS is ["seed"],
            $"a rejected ADD must leave the set exactly as it was, got {Py.Repr(afterAdd)}");
    }

    private static async Task<Dictionary<string, AttributeValue>> Get(IAmazonDynamoDB ddb, string table, string pk) =>
        (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Item(("pk", S(pk))), ConsistentRead = true })).Item ?? [];

    /// <summary>
    /// The botocore error code and message <paramref name="fn"/> raises.
    ///
    /// An unexpected SUCCESS raises rather than returning a falsy pair: a responder that
    /// ACCEPTS a growth real AWS refuses is the strongest signal this check can produce.
    /// </summary>
    private static async Task<(string? Code, string? Message)> CodeAndMessage(Func<Task<UpdateItemResponse>> fn)
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is not null)
        {
            return (error.Code, error.Message);
        }

        throw Fail($"real AWS rejects this UpdateItem, but it SUCCEEDED and returned {Py.Repr(result?.Attributes)}");
    }
}
