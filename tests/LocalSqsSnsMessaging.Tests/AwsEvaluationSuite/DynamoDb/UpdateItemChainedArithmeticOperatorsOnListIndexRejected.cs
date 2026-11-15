// Translated from behavioral/dynamodb/check_update_item_chained_arithmetic_operators_on_list_index_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Ddb;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.update-item-chained-arithmetic-operators-on-list-index-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class UpdateItemChainedArithmeticOperatorsOnListIndexRejected
{
    private const string CheckId = "update-item-chained-arithmetic-operators-on-list-index-rejected";
    private const string ErrValidation = "ValidationException";
    private const string MsgChainedOp =
        "1 validation error detected: Invalid UpdateExpression: Syntax error; token: \"-\", near: \") - :y\"";

    /// <summary>
    /// A SET operand on a list index chaining two arithmetic operators
    /// (a[0] + :x) - :y is a ValidationException syntax error and leaves the list unchanged, while
    /// a single operator wrapped in one set of parentheses on the same index succeeds.
    /// </summary>
    [Test]
    [KnownFidelityGap(FidelityGap.DynamoDbDetail, "The emulator accepts SET a[0] = (a[0] + :x) - :y, which DynamoDB refuses as a syntax error")]
    [DisplayName("dynamodb.update-item-chained-arithmetic-operators-on-list-index-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-ddb-chainop-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real", Value = CheckId }],
        });
        ctx.OnCleanup(async () =>
        {
            try
            {
                await ddb.DeleteTableAsync(table);
            }
            catch (ResourceNotFoundException)
            {
            }
        });
        await WaitActive(ddb, ctx, table);

        var key = Item(("pk", S("doc")));
        var original = Item(("pk", S("doc")), ("a", L(N("10"), N("20"), N("30"))));
        await ddb.PutItemAsync(table, original);

        var (code, message) = await Error(() => ddb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = table,
            Key = key,
            UpdateExpression = "SET a[0] = (a[0] + :x) - :y",
            ExpressionAttributeValues = Item((":x", N("5")), (":y", N("3"))),
        }));
        Ensure(
            code == ErrValidation,
            $"SET a[0] = (a[0] + :x) - :y answered {Py.Repr(code)} ({Py.Repr(message)}), expected {ErrValidation}");
        Ensure(message == MsgChainedOp, $"the refusal's message is {Py.Repr(message)}, expected {Py.Repr(MsgChainedOp)}");
        var after = (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = key, ConsistentRead = true })).Item;
        Ensure(Py.Equal(after, original), $"after the refused SET the item is {Py.Repr(after)}, expected it unchanged: {Py.Repr(original)}");

        // Positive control: a single operator wrapped in one set of parentheses on the same
        // list index succeeds.
        var got = (await ddb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = table,
            Key = key,
            UpdateExpression = "SET a[0] = (a[0] + :x)",
            ExpressionAttributeValues = Item((":x", N("5"))),
            ReturnValues = ReturnValue.UPDATED_NEW,
        })).Attributes;
        Ensure(Py.Equal(got, Item(("a", L(N("15"))))), $"SET a[0] = (a[0] + :x) on the same index returned {Py.Repr(got)}");
        var final = (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = key, ConsistentRead = true })).Item;
        Ensure(
            Py.Equal(final, Item(("pk", S("doc")), ("a", L(N("15"), N("20"), N("30"))))),
            $"final list is {Py.Repr(final.GetValueOrDefault("a"))}, expected [15, 20, 30]");
    }

    /// <summary>Poll DescribeTable every 2 s until the table is ACTIVE.</summary>
    private static async Task<TableDescription> WaitActive(IAmazonDynamoDB ddb, FidelityContext ctx, string table)
    {
        var timeLeft = ctx.TimeLeftSeconds;
        var budget = Math.Max(0.0, (timeLeft > 0 ? timeLeft : 300.0) - 30.0);
        var clock = FidelityTime.StartNew();
        TableDescription? desc = null;
        while (clock.Elapsed.TotalSeconds < budget)
        {
            desc = (await ddb.DescribeTableAsync(new DescribeTableRequest { TableName = table })).Table;
            if (desc.TableStatus == TableStatus.ACTIVE)
            {
                return desc;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(2));
        }

        throw Fail($"table {table} not ACTIVE before the deadline: {Py.Repr(desc?.TableStatus)}");
    }

    /// <summary>(code, message) of the error fn() raises; raises if fn() unexpectedly succeeds.</summary>
    private static async Task<(string? Code, string? Message)> Error<T>(Func<Task<T>> fn)
        where T : class
    {
        var (resp, error) = await FidelityContext.Capture(fn);
        return error is null
            ? throw Fail($"call unexpectedly succeeded where an error was required: {Py.Repr(resp)}")
            : (error.Code, error.Message);
    }
}
