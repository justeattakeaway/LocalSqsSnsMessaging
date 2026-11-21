// Translated from behavioral/dynamodb/check_update_item_add_list_index_type_mismatch_and_missing_attribute_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit abd0811).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.update-item-add-list-index-type-mismatch-and-missing-attribute-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class UpdateItemAddListIndexTypeMismatchAndMissingAttributeRejected
{
    private const string Check = "update-item-add-list-index-type-mismatch-and-missing-attribute-rejected";
    private const string ErrValidation = "ValidationException";
    private const string MsgTypeMismatch = "An operand in the update expression has an incorrect data type";
    private const string MsgPathInvalid = "The document path provided in the update expression is invalid for update";

    /// <summary>A cleanup running undo() where a failed delete raises.</summary>
    private static Func<Task> Teardown(Func<Task> undo, params string[] notFound) => async () =>
    {
        var error = await FidelityContext.CaptureError(undo);
        if (error is not null && !notFound.Contains(error.Code))
        {
            throw error.Exception;
        }
    };

    /// <summary>Poll DescribeTable every 2 s until the table is ACTIVE.</summary>
    private static async Task<TableDescription> WaitActive(IAmazonDynamoDB ddb, FidelityContext ctx, string table)
    {
        var left = ctx.TimeLeftSeconds;
        var bound = Math.Max(0.0, (left == 0 ? 300.0 : left) - 30.0);
        var clock = FidelityTime.StartNew();
        TableDescription? desc = null;
        while (clock.Elapsed.TotalSeconds < bound)
        {
            desc = (await ddb.DescribeTableAsync(new DescribeTableRequest { TableName = table })).Table;
            if (desc.TableStatus == TableStatus.ACTIVE)
            {
                return desc;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(2));
        }

        throw Fail($"table {table} not ACTIVE before the deadline: {Py.Repr(desc)}");
    }

    /// <summary>(code, message) of the error fn() raises; raises if fn() unexpectedly succeeds.</summary>
    private static async Task<(string? Code, string? Message)> Error(Func<Task<object>> fn)
    {
        var (resp, err) = await FidelityContext.Capture(fn);
        if (err is null)
        {
            throw Fail($"call unexpectedly succeeded where an error was required: {resp}");
        }

        return (err.Code, err.Message);
    }

    /// <summary>
    /// ADD on an in-bounds list index with a wrong-type operand, and ADD through a list index
    /// whose top-level attribute is absent, both reject with ValidationException and leave the
    /// item unchanged, while the matching-type and existing-attribute forms succeed.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.update-item-add-list-index-type-mismatch-and-missing-attribute-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-ddb-addrej-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real", Value = Check }],
        });
        ctx.OnCleanup(Teardown(() => ddb.DeleteTableAsync(table), "ResourceNotFoundException"));
        await WaitActive(ddb, ctx, table);

        async Task<Dictionary<string, AttributeValue>> Get(Dictionary<string, AttributeValue> key) =>
            (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = key, ConsistentRead = true })).Item;

        // ADD a Set operand onto an in-bounds Number list element.
        var keyN = Item(("pk", S("num-elem")));
        var originalN = Item(("pk", S("num-elem")), ("a", L(N("1"), N("2"))));
        await ddb.PutItemAsync(table, originalN);
        var (code, message) = await Error(async () => await ddb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = table,
            Key = keyN,
            UpdateExpression = "ADD a[0] :v",
            ExpressionAttributeValues = Item((":v", SS("x"))),
        }));
        Ensure(
            code == ErrValidation,
            $"ADD a[0] :v with a Set operand on a Number element answered {Py.Repr(code)} ({Py.Repr(message)}), " +
            $"expected {ErrValidation}");
        Ensure(message == MsgTypeMismatch, $"the refusal's message is {Py.Repr(message)}, expected {Py.Repr(MsgTypeMismatch)}");
        var after = await Get(keyN);
        Ensure(Py.Equal(after, originalN), $"after the refused ADD the item is {Py.Repr(after)}, expected it unchanged: {Py.Repr(originalN)}");

        // ADD a Number operand onto an in-bounds String list element: the mismatch runs the other
        // direction and answers with the same code and message.
        var keyS = Item(("pk", S("str-elem")));
        var originalS = Item(("pk", S("str-elem")), ("a", L(S("x"), N("2"))));
        await ddb.PutItemAsync(table, originalS);
        (code, message) = await Error(async () => await ddb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = table,
            Key = keyS,
            UpdateExpression = "ADD a[0] :v",
            ExpressionAttributeValues = Item((":v", N("5"))),
        }));
        Ensure(
            code == ErrValidation,
            $"ADD a[0] :v with a Number operand on a String element answered {Py.Repr(code)} ({Py.Repr(message)}), " +
            $"expected {ErrValidation}");
        Ensure(message == MsgTypeMismatch, $"the refusal's message is {Py.Repr(message)}, expected {Py.Repr(MsgTypeMismatch)}");
        after = await Get(keyS);
        Ensure(Py.Equal(after, originalS), $"after the refused ADD the item is {Py.Repr(after)}, expected it unchanged: {Py.Repr(originalS)}");

        // ADD through a list index whose top-level attribute is entirely absent (not merely a
        // wrong-shape parent): the whole document path is invalid for update.
        var keyMissing = Item(("pk", S("missing-attr")));
        var originalMissing = Item(("pk", S("missing-attr")));
        await ddb.PutItemAsync(table, originalMissing);
        (code, message) = await Error(async () => await ddb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = table,
            Key = keyMissing,
            UpdateExpression = "ADD b[5] :v",
            ExpressionAttributeValues = Item((":v", N("1"))),
        }));
        Ensure(
            code == ErrValidation,
            $"ADD b[5] :v on an absent top-level attribute answered {Py.Repr(code)} ({Py.Repr(message)}), expected " +
            $"{ErrValidation}");
        Ensure(message == MsgPathInvalid, $"the refusal's message is {Py.Repr(message)}, expected {Py.Repr(MsgPathInvalid)}");
        after = await Get(keyMissing);
        Ensure(
            Py.Equal(after, originalMissing),
            $"after the refused ADD the item is {Py.Repr(after)}, expected it unchanged: {Py.Repr(originalMissing)}");

        // Positive controls: the same shapes succeed when the type matches and the attribute exists.
        var got = (await ddb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = table,
            Key = keyN,
            UpdateExpression = "ADD a[0] :v",
            ExpressionAttributeValues = Item((":v", N("5"))),
            ReturnValues = ReturnValue.ALL_NEW,
        })).Attributes;
        Ensure(
            Py.Equal(got, Item(("pk", S("num-elem")), ("a", L(N("6"), N("2"))))),
            $"matching-type ADD a[0] :v returned {Py.Repr(got)}, expected a[0] to become 6");

        got = (await ddb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = table,
            Key = keyMissing,
            UpdateExpression = "SET a = :empty",
            ExpressionAttributeValues = Item((":empty", L())),
            ReturnValues = ReturnValue.ALL_NEW,
        })).Attributes;
        Ensure(Py.Equal(got, Item(("pk", S("missing-attr")), ("a", L()))), $"seeding an empty list for 'a' returned {Py.Repr(got)}");
        got = (await ddb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = table,
            Key = keyMissing,
            UpdateExpression = "ADD a[9] :v",
            ExpressionAttributeValues = Item((":v", N("1"))),
            ReturnValues = ReturnValue.ALL_NEW,
        })).Attributes;
        Ensure(
            Py.Equal(got, Item(("pk", S("missing-attr")), ("a", L(N("1"))))),
            $"ADD a[9] :v once 'a' exists returned {Py.Repr(got)}, expected the operand appended");
    }
}
