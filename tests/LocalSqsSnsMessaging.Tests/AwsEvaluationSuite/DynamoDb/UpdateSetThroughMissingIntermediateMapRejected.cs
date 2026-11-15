// Translated from behavioral/dynamodb/check_update_set_through_missing_intermediate_map_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Ddb;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.update-set-through-missing-intermediate-map-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class UpdateSetThroughMissingIntermediateMapRejected
{
    private const string PathInvalid = "The document path provided in the update expression is invalid for update";

    /// <summary>
    /// SET a.b.c is refused when map a exists but a.b does not, and the item is unchanged; once
    /// a.b is a map the same SET succeeds.
    ///
    /// Reference: https://docs.aws.amazon.com/amazondynamodb/latest/developerguide/Expressions.UpdateExpressions.html
    /// </summary>
    [Test]
    [DisplayName("dynamodb.update-set-through-missing-intermediate-map-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-ddb-midpath-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
        });
        ctx.OnCleanup(Teardown(() => ddb.DeleteTableAsync(table), "ResourceNotFoundException"));
        await WaitActive(ddb, ctx, table);

        var key = Item(("pk", S("deep-miss")));
        var seeded = Item(("pk", S("deep-miss")), ("a", M(("x", S("y")))));
        await ddb.PutItemAsync(table, seeded);

        var (result, error) = await FidelityContext.Capture(() => ddb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = table,
            Key = key,
            UpdateExpression = "SET a.b.c = :v",
            ExpressionAttributeValues = Item((":v", S("deep"))),
        }));
        if (error is null)
        {
            throw Fail($"expected the call to be refused, but it succeeded: {Py.Repr(result?.Attributes)}");
        }

        var (code, msg) = (error.Code, error.Message);
        Ensure(
            (code, msg) == ("ValidationException", PathInvalid),
            $"SET a.b.c with a present but a.b absent gave {Py.Repr(code)}: {Py.Repr(msg)}");
        var item = (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = key, ConsistentRead = true })).Item;
        Ensure(Py.Equal(item, seeded), $"the refused SET a.b.c changed the item to {Py.Repr(item)}");

        // Positive control: create the intermediate map, then the same SET is legal.
        await ddb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = table,
            Key = key,
            UpdateExpression = "SET a.b = :m",
            ExpressionAttributeValues = Item((":m", M())),
        });
        await ddb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = table,
            Key = key,
            UpdateExpression = "SET a.b.c = :v",
            ExpressionAttributeValues = Item((":v", S("deep"))),
        });
        item = (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = key, ConsistentRead = true })).Item;
        var want = Item(("pk", S("deep-miss")), ("a", M(("x", S("y")), ("b", M(("c", S("deep")))))));
        Ensure(Py.Equal(item, want), $"after creating a.b the item is {Py.Repr(item)}, expected {Py.Repr(want)}");
    }

    /// <summary>A cleanup running undo() where a failed delete raises; in-use is retried every 2 s.</summary>
    private static Func<Task> Teardown(Func<Task> undo, params string[] notFound) => async () =>
    {
        var clock = FidelityTime.StartNew();
        while (true)
        {
            try
            {
                await undo();
                return;
            }
            catch (AmazonDynamoDBException ex)
            {
                if (notFound.Contains(ex.ErrorCode, StringComparer.Ordinal))
                {
                    return;
                }

                if (ex.ErrorCode != "ResourceInUseException" || clock.Elapsed >= TimeSpan.FromSeconds(300))
                {
                    throw;
                }
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(2));
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
}
