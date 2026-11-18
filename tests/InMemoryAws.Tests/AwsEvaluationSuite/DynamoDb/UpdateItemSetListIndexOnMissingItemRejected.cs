// Translated from behavioral/dynamodb/check_update_item_set_list_index_on_missing_item_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.update-item-set-list-index-on-missing-item-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class UpdateItemSetListIndexOnMissingItemRejected
{
    private const string Check = "update-item-set-list-index-on-missing-item-rejected";
    private const string ErrValidation = "ValidationException";
    private const string MsgInvalidPath = "The document path provided in the update expression is invalid for update";

    /// <summary>
    /// SET b[1] on a key holding no item is rejected with ValidationException and creates no
    /// item; the same expression succeeds once the item exists with a list attribute b.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.update-item-set-list-index-on-missing-item-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var t = await Table(ctx, ddb);
        var key = Item(("pk", S("no-item")));
        var vals = Item((":v", S("x")));

        Ensure(await Stored(ddb, t, "no-item") is null, "the key already holds an item before the test");
        var (code, msg) = await Error(() => ddb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = t,
            Key = key,
            UpdateExpression = "SET b[1] = :v",
            ExpressionAttributeValues = vals,
        }));
        Ensure(code == ErrValidation, $"SET b[1] on a missing item: expected {ErrValidation}, got {Py.Repr(code)} ({Py.Repr(msg)})");
        Ensure(msg == MsgInvalidPath, $"SET b[1] on a missing item: expected {Py.Repr(MsgInvalidPath)}, got {Py.Repr(msg)}");
        Ensure(await Stored(ddb, t, "no-item") is null, "the rejected SET b[1] created an item anyway");

        // Positive control: once the item exists with b as a list, the same bracket-indexed SET works.
        var key2 = Item(("pk", S("has-item")));
        await ddb.PutItemAsync(t, Item(("pk", S("has-item")), ("b", L(S("v0")))));
        var resp = await ddb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = t,
            Key = key2,
            UpdateExpression = "SET b[1] = :v",
            ExpressionAttributeValues = vals,
            ReturnValues = ReturnValue.ALL_NEW,
        });
        Ensure(
            Py.Equal(resp.Attributes, Item(("pk", S("has-item")), ("b", L(S("v0"), S("x"))))),
            $"SET b[1] once the item and list exist returned ALL_NEW {Py.Repr(resp.Attributes)}, " +
            "expected b=[v0, x]");
    }

    private static async Task<string> Table(FidelityContext ctx, IAmazonDynamoDB ddb)
    {
        var name = FidelityContext.UniqueName("sim2real-behav-ddbnp-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = name,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real", Value = Check }],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(name));
        await ddb.WaitForTableExists(name, delaySeconds: 2, maxAttempts: 250);
        return name;
    }

    private static async Task<Dictionary<string, AttributeValue>?> Stored(IAmazonDynamoDB ddb, string table, string pk) =>
        (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Item(("pk", S(pk))), ConsistentRead = true })).Item;

    /// <summary>(code, message) of the ClientError fn raises. Raises if fn SUCCEEDS.</summary>
    private static async Task<(string? Code, string? Message)> Error(Func<Task<UpdateItemResponse>> fn)
    {
        var (resp, error) = await FidelityContext.Capture(fn);
        if (error is not null)
        {
            return (error.Code, error.Message);
        }

        throw Fail($"call was expected to be rejected but succeeded: {Py.Repr(resp?.Attributes)}");
    }
}
