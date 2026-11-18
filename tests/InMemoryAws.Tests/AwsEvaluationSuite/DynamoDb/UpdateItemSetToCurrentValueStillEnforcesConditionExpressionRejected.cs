// Translated from behavioral/dynamodb/check_update_item_set_to_current_value_still_enforces_condition_expression_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.update-item-set-to-current-value-still-enforces-condition-expression-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class UpdateItemSetToCurrentValueStillEnforcesConditionExpressionRejected
{
    private const string ErrConditionalCheckFailed = "ConditionalCheckFailedException";

    /// <summary>
    /// A SET that re-assigns an attribute's own current value is still gated by the request's
    /// ConditionExpression: a failing condition rejects it and leaves the item untouched.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.update-item-set-to-current-value-still-enforces-condition-expression-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-ddb-noop-cond-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
        });
        ctx.OnCleanup(Teardown(() => ddb.DeleteTableAsync(table), "ResourceNotFoundException"));
        await WaitActive(ddb, ctx, table);

        var key = Item(("pk", S("cond")));
        var original = Item(("pk", S("cond")), ("val", S("same")), ("cnt", N("1")));
        await ddb.PutItemAsync(table, original);

        var (code, message) = await CodeAndMessage(() => ddb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = table,
            Key = key,
            UpdateExpression = "SET val = :same",
            ConditionExpression = "val = :wrong",
            ExpressionAttributeValues = Item((":same", S("same")), (":wrong", S("different"))),
        }));
        Ensure(
            code == ErrConditionalCheckFailed,
            "a no-op SET must still be gated by a failing ConditionExpression; expected " +
            $"{ErrConditionalCheckFailed}, got {Py.Repr(code)}");
        Ensure(
            message == "The conditional request failed",
            $"expected AWS's exact ConditionalCheckFailedException message, got {Py.Repr(message)}");

        var unchanged = await Get(ddb, table, key);
        Ensure(Py.Equal(unchanged, original), $"a rejected no-op SET must not touch the item at all, got {Py.Repr(unchanged)}");

        var resp = await ddb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = table,
            Key = key,
            UpdateExpression = "SET val = :same, #c = :newcnt",
            ConditionExpression = "val = :same",
            ExpressionAttributeNames = Names(("#c", "cnt")),
            ExpressionAttributeValues = Item((":same", S("same")), (":newcnt", N("2"))),
            ReturnValues = ReturnValue.UPDATED_NEW,
        });
        var attrs = resp.Attributes;
        Ensure(
            Py.Equal(attrs, Item(("val", S("same")), ("cnt", N("2")))),
            "a matching condition must let the no-op SET through alongside the real change, and " +
            $"UPDATED_NEW must list both, got {Py.Repr(attrs)}");

        var final = await Get(ddb, table, key);
        Ensure(
            Py.Equal(final, Item(("pk", S("cond")), ("val", S("same")), ("cnt", N("2")))),
            $"independent GetItem after the accepted update must show the new state, got {Py.Repr(final)}");
    }

    /// <summary>A cleanup running undo() where a failed delete raises.</summary>
    private static Func<Task> Teardown(Func<Task> undo, params string[] notFound) => async () =>
    {
        try
        {
            await undo();
        }
        catch (AmazonDynamoDBException ex) when (notFound.Contains(ex.ErrorCode, StringComparer.Ordinal))
        {
        }
    };

    /// <summary>Poll DescribeTable every 2 s until the table is ACTIVE.</summary>
    private static async Task<TableDescription> WaitActive(IAmazonDynamoDB ddb, FidelityContext ctx, string table)
    {
        var timeLeft = ctx.TimeLeftSeconds;
        var deadline = TimeSpan.FromSeconds(Math.Max(0.0, (timeLeft > 0 ? timeLeft : 300.0) - 30.0));
        var clock = FidelityTime.StartNew();
        TableDescription? desc = null;
        while (clock.Elapsed < deadline)
        {
            desc = (await ddb.DescribeTableAsync(table)).Table;
            if (desc.TableStatus == TableStatus.ACTIVE)
            {
                return desc;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(2));
        }

        throw Fail($"table {table} not ACTIVE before the deadline: {Py.Repr(desc?.TableStatus)}");
    }

    private static async Task<Dictionary<string, AttributeValue>?> Get(IAmazonDynamoDB ddb, string table, Dictionary<string, AttributeValue> key) =>
        (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = key, ConsistentRead = true })).Item;

    /// <summary>The (code, message) <paramref name="fn"/> raises; fails if the call unexpectedly succeeds.</summary>
    private static async Task<(string? Code, string? Message)> CodeAndMessage(Func<Task<UpdateItemResponse>> fn)
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is not null)
        {
            return (error.Code, error.Message);
        }

        throw Fail($"expected the call to be refused, but it succeeded: {Py.Repr(result?.Attributes)}");
    }
}
