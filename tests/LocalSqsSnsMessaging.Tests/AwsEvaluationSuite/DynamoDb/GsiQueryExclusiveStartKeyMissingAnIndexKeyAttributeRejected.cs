// Translated from behavioral/dynamodb/check_gsi_query_exclusive_start_key_missing_an_index_key_attribute_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Globalization;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Ddb;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.gsi-query-exclusive-start-key-missing-an-index-key-attribute-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class GsiQueryExclusiveStartKeyMissingAnIndexKeyAttributeRejected
{
    private const string CheckId = "gsi-query-exclusive-start-key-missing-an-index-key-attribute-rejected";
    private const string Index = "StatusIndex";
    private const string ErrValidation = "ValidationException";
    private static readonly string[] MissingAttributes = ["CustomerId", "OrderId", "Status", "OrderSeq"];

    private static async Task WaitActive(IAmazonDynamoDB ddb, string table, double timeoutSeconds = 180.0)
    {
        var clock = FidelityTime.StartNew();
        TableDescription? last = null;
        while (clock.Elapsed < TimeSpan.FromSeconds(timeoutSeconds))
        {
            last = (await ddb.DescribeTableAsync(table)).Table;
            var indexes = last.GlobalSecondaryIndexes ?? [];
            if (last.TableStatus == TableStatus.ACTIVE && indexes.Count > 0 && indexes.All(g => g.IndexStatus == IndexStatus.ACTIVE))
            {
                return;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(3));
        }

        throw Fail($"table {table} or its index did not become ACTIVE: {Py.Repr(last)}");
    }

    /// <summary><c>(Code, Message)</c> for the <c>ClientError</c> <c>fn</c> raises; an unexpected success raises too.</summary>
    private static async Task<(string? Code, string? Message)> Error<T>(Func<Task<T>> fn)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is not null)
        {
            return (error.Code, error.Message);
        }

        throw Fail($"expected the call to be rejected, but it succeeded: {Py.Repr(result)}");
    }

    private static string OrderId(int i) => "order-" + i.ToString("000", CultureInfo.InvariantCulture);

    /// <summary>
    /// A GSI Query's ExclusiveStartKey missing any one of the table's two key attributes or the
    /// index's two is rejected with ValidationException, for all four attributes, while a complete
    /// ExclusiveStartKey continues normally and no rejected call changes the table's contents.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.gsi-query-exclusive-start-key-missing-an-index-key-attribute-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-gsilekneg-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            KeySchema = [new KeySchemaElement("CustomerId", KeyType.HASH), new KeySchemaElement("OrderId", KeyType.RANGE)],
            AttributeDefinitions =
            [
                new AttributeDefinition("CustomerId", ScalarAttributeType.S),
                new AttributeDefinition("OrderId", ScalarAttributeType.S),
                new AttributeDefinition("Status", ScalarAttributeType.S),
                new AttributeDefinition("OrderSeq", ScalarAttributeType.N),
            ],
            GlobalSecondaryIndexes =
            [
                new GlobalSecondaryIndex
                {
                    IndexName = Index,
                    KeySchema = [new KeySchemaElement("Status", KeyType.HASH), new KeySchemaElement("OrderSeq", KeyType.RANGE)],
                    Projection = new Projection { ProjectionType = ProjectionType.ALL },
                },
            ],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real", Value = CheckId }],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(table));
        await WaitActive(ddb, table);

        for (var i = 0; i < 5; i++)
        {
            await ddb.PutItemAsync(table, Item(
                ("CustomerId", S("cust-01")),
                ("OrderId", S(OrderId(i))),
                ("Status", S("PLACED")),
                ("OrderSeq", N(i.ToString(CultureInfo.InvariantCulture)))));
        }

        Task<QueryResponse> Query(int? limit = null, Dictionary<string, AttributeValue>? exclusiveStartKey = null) => ddb.QueryAsync(new QueryRequest
        {
            TableName = table,
            IndexName = Index,
            KeyConditionExpression = "#s = :s",
            ExpressionAttributeNames = Names(("#s", "Status")),
            ExpressionAttributeValues = Item((":s", S("PLACED"))),
            Limit = limit,
            ExclusiveStartKey = exclusiveStartKey,
        });

        // The GSI is eventually consistent: poll until the index holds all five items.
        var timeLeft = ctx.TimeLeftSeconds;
        var deadline = Math.Max(0.0, (timeLeft > 0 ? timeLeft : 300.0) - 60.0);
        var clock = FidelityTime.StartNew();
        while ((await Query()).Count != 5)
        {
            Ensure(clock.Elapsed.TotalSeconds < deadline, "the index never held all five items");
            await FidelityTime.Delay(TimeSpan.FromSeconds(2));
        }

        var page = await Query(limit: 1);
        var pageIds = page.Items.Select(i => i["OrderId"].S).ToList();
        Ensure(pageIds.SequenceEqual(["order-000"]), Py.Repr(page.Items));
        var realKey = page.LastEvaluatedKey;
        Ensure(
            Py.Equal(realKey, Item(("CustomerId", S("cust-01")), ("OrderId", S("order-000")), ("Status", S("PLACED")), ("OrderSeq", N("0")))),
            $"expected the first item's four key attributes as the LastEvaluatedKey, got {Py.Repr(realKey)}");

        // Positive control: the real, complete ExclusiveStartKey continues the walk.
        var cont = await Query(exclusiveStartKey: realKey);
        var contIds = cont.Items.Select(i => i["OrderId"].S).ToList();
        Ensure(
            contIds.SequenceEqual(Enumerable.Range(1, 4).Select(OrderId)),
            $"positive control: the real ExclusiveStartKey must resume after order-000, got {Py.Repr(contIds)}");

        foreach (var missing in MissingAttributes)
        {
            var badKey = realKey.Where(e => e.Key != missing).ToDictionary(e => e.Key, e => e.Value, StringComparer.Ordinal);
            var (code, message) = await Error(() => Query(exclusiveStartKey: badKey));
            Ensure(
                code == ErrValidation,
                $"ExclusiveStartKey missing {Py.Repr(missing)}: expected {ErrValidation}, got {code}: {Py.Repr(message)}");
            Ensure(
                message == "The provided starting key is invalid",
                $"ExclusiveStartKey missing {Py.Repr(missing)}: expected AWS's exact ValidationException message, got {Py.Repr(message)}");
        }

        // None of the four rejected calls wrote, deleted or otherwise changed the table.
        var final = (await ddb.ScanAsync(new ScanRequest { TableName = table, ConsistentRead = true })).Items;
        var finalIds = final.Select(i => i["OrderId"].S).Order(StringComparer.Ordinal).ToList();
        Ensure(
            finalIds.SequenceEqual(Enumerable.Range(0, 5).Select(OrderId)),
            $"expected the original 5 items unchanged after the rejected calls, got {Py.Repr(finalIds)}");

        // A second positive control: the real key still continues the walk after the rejections.
        var cont2 = await Query(exclusiveStartKey: realKey);
        Ensure(
            Py.Equal(cont2.Items, cont.Items),
            $"the real ExclusiveStartKey's continuation changed after the rejected calls: {Py.Repr(cont2.Items)} vs {Py.Repr(cont.Items)}");
    }
}
