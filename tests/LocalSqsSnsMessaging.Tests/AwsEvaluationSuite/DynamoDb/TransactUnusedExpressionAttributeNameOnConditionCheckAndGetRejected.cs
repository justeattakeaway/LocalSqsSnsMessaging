// Translated from behavioral/dynamodb/check_transact_unused_expression_attribute_name_on_condition_check_and_get_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Ddb;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.transact-unused-expression-attribute-name-on-condition-check-and-get-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class TransactUnusedExpressionAttributeNameOnConditionCheckAndGetRejected
{
    private const string UnusedName = "Value provided in ExpressionAttributeNames unused in expressions: keys: {#n}";

    /// <summary>
    /// An unused ExpressionAttributeNames entry on a ConditionCheck or a projected Get is a
    /// top-level ValidationException; the transaction writes nothing and the used form succeeds.
    ///
    /// Reference: https://docs.aws.amazon.com/amazondynamodb/latest/developerguide/Expressions.ExpressionAttributeNames.html
    /// </summary>
    [Test]
    [DisplayName("dynamodb.transact-unused-expression-attribute-name-on-condition-check-and-get-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = await CreateTable(ctx, ddb, "sim2real-behav-ddb-txun-");
        await ddb.PutItemAsync(table, Item(("pk", S("seed")), ("color", S("red"))));
        Dictionary<string, AttributeValue> NewKey() => Item(("pk", S("new")));

        Task<TransactWriteItemsResponse> Write(Dictionary<string, string> names) =>
            ddb.TransactWriteItemsAsync(new TransactWriteItemsRequest
            {
                TransactItems =
                [
                    new TransactWriteItem
                    {
                        ConditionCheck = new ConditionCheck
                        {
                            TableName = table,
                            Key = Item(("pk", S("seed"))),
                            ConditionExpression = "attribute_exists(pk)",
                            ExpressionAttributeNames = names,
                        },
                    },
                    new TransactWriteItem { Put = new Put { TableName = table, Item = Item(("pk", S("new")), ("v", S("1"))) } },
                ],
            });

        var (code, msg, reasons) = await Error(() => Write(Names(("#n", "color"))));
        Ensure(
            (code, msg) == ("ValidationException", UnusedName),
            $"ConditionCheck with an unused name gave {Py.Repr(code)}: {Py.Repr(msg)}");
        Ensure(reasons is null, $"a top-level ValidationException carried CancellationReasons {Py.Repr(reasons)}");
        Ensure(await Get(ddb, table, NewKey()) is null, "a refused transaction wrote its Put");

        Task<TransactGetItemsResponse> Read(string projection, Dictionary<string, string> names) =>
            ddb.TransactGetItemsAsync(new TransactGetItemsRequest
            {
                TransactItems =
                [
                    new TransactGetItem
                    {
                        Get = new Get
                        {
                            TableName = table,
                            Key = Item(("pk", S("seed"))),
                            ProjectionExpression = projection,
                            ExpressionAttributeNames = names,
                        },
                    },
                ],
            });

        (code, msg, reasons) = await Error(() => Read("pk", Names(("#n", "color"))));
        Ensure(
            (code, msg) == ("ValidationException", UnusedName),
            $"TransactGetItems Get with an unused name gave {Py.Repr(code)}: {Py.Repr(msg)}");
        Ensure(reasons is null, $"a top-level ValidationException carried CancellationReasons {Py.Repr(reasons)}");

        var resp = await Read("#n", Names(("#n", "color")));
        var slots = resp.Responses.Select(r => r.Item is null ? [] : new Dictionary<string, object?> { ["Item"] = r.Item }).ToList();
        Ensure(
            Py.Equal(slots, new List<object?> { new Dictionary<string, object?> { ["Item"] = Item(("color", S("red"))) } }),
            $"TransactGetItems projecting #n=color returned {Py.Repr(slots)}");
        await ddb.TransactWriteItemsAsync(new TransactWriteItemsRequest
        {
            TransactItems =
            [
                new TransactWriteItem
                {
                    ConditionCheck = new ConditionCheck
                    {
                        TableName = table,
                        Key = Item(("pk", S("seed"))),
                        ConditionExpression = "attribute_exists(#n)",
                        ExpressionAttributeNames = Names(("#n", "color")),
                    },
                },
                new TransactWriteItem { Put = new Put { TableName = table, Item = Item(("pk", S("new")), ("v", S("1"))) } },
            ],
        });
        var got = await Get(ddb, table, NewKey());
        Ensure(Py.Equal(got, Item(("pk", S("new")), ("v", S("1")))), $"the transaction with every name used stored {Py.Repr(got)}");
    }

    /// <summary>A cleanup running undo() where a failed delete raises; in-use is retried every 2 s.</summary>
    private static Func<Task> Teardown(Func<Task> undo, string[] notFound, double timeoutSeconds = 300.0) => async () =>
    {
        var clock = FidelityTime.StartNew();
        while (true)
        {
            var error = await FidelityContext.CaptureError(undo);
            if (error is null || notFound.Contains(error.Code))
            {
                return;
            }

            if (error.Code != "ResourceInUseException" || clock.Elapsed.TotalSeconds >= timeoutSeconds)
            {
                throw error.Exception;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(2));
        }
    };

    /// <summary>Poll DescribeTable every 2 s until the table and its indexes are ACTIVE.</summary>
    private static async Task<TableDescription> WaitActive(IAmazonDynamoDB ddb, FidelityContext ctx, string table)
    {
        var clock = FidelityTime.StartNew();
        var timeLeft = ctx.TimeLeftSeconds;
        var deadline = Math.Max(0.0, (timeLeft > 0 ? timeLeft : 300.0) - 30.0);
        TableDescription? desc = null;
        while (clock.Elapsed.TotalSeconds < deadline)
        {
            desc = (await ddb.DescribeTableAsync(table)).Table;
            if (desc.TableStatus == TableStatus.ACTIVE
                && (desc.GlobalSecondaryIndexes ?? []).All(g => g.IndexStatus == IndexStatus.ACTIVE))
            {
                return desc;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(2));
        }

        throw Fail($"table {table} not ACTIVE before the deadline: {desc?.TableStatus}");
    }

    /// <summary>Create an on-demand table keyed pk (S); wait ACTIVE.</summary>
    private static async Task<string> CreateTable(FidelityContext ctx, IAmazonDynamoDB ddb, string prefix)
    {
        var table = FidelityContext.UniqueName(prefix);
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
        });
        ctx.OnCleanup(Teardown(() => ddb.DeleteTableAsync(table), ["ResourceNotFoundException"]));
        await WaitActive(ddb, ctx, table);
        return table;
    }

    /// <summary>(Error.Code, Error.Message, CancellationReasons) of the error <paramref name="fn"/> raises.</summary>
    private static async Task<(string? Code, string? Message, List<CancellationReason>? Reasons)> Error(Func<Task> fn)
    {
        var error = await FidelityContext.CaptureError(fn)
            ?? throw Fail("expected the call to be refused, but it succeeded");
        return (error.Code, error.Message, error.CancellationReasons());
    }

    /// <summary>The item at key read with ConsistentRead, or null.</summary>
    private static async Task<Dictionary<string, AttributeValue>?> Get(IAmazonDynamoDB ddb, string table, Dictionary<string, AttributeValue> key) =>
        (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = key, ConsistentRead = true })).Item;
}
