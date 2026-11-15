// Translated from behavioral/dynamodb/check_item_collection_metrics_reported_only_for_lsi_tables.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Ddb;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.item-collection-metrics-reported-only-for-lsi-tables</summary>
[Category("AwsEvaluationSuite")]
public sealed class ItemCollectionMetricsReportedOnlyForLsiTables
{
    private const string CheckId = "item-collection-metrics-reported-only-for-lsi-tables";

    private static Dictionary<string, object?>? Repr(ItemCollectionMetrics? metrics) => metrics is null
        ? null
        : new Dictionary<string, object?> { ["ItemCollectionKey"] = metrics.ItemCollectionKey, ["SizeEstimateRangeGB"] = metrics.SizeEstimateRangeGB };

    /// <summary>
    /// ReturnItemCollectionMetrics=SIZE on a table with an LSI reports the partition key alone and an ordered
    /// two-value size estimate; a table without an LSI, or a write that does not request them, reports none.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.item-collection-metrics-reported-only-for-lsi-tables")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;

        var withLsi = FidelityContext.UniqueName("sim2real-behav-lsi-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = withLsi,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH), new KeySchemaElement("sk", KeyType.RANGE)],
            AttributeDefinitions =
            [
                new AttributeDefinition("pk", ScalarAttributeType.S),
                new AttributeDefinition("sk", ScalarAttributeType.N),
                new AttributeDefinition("li", ScalarAttributeType.S),
            ],
            LocalSecondaryIndexes =
            [
                new LocalSecondaryIndex
                {
                    IndexName = "lsi1",
                    KeySchema = [new KeySchemaElement("pk", KeyType.HASH), new KeySchemaElement("li", KeyType.RANGE)],
                    Projection = new Projection { ProjectionType = ProjectionType.ALL },
                },
            ],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real", Value = CheckId }],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(withLsi));

        var withoutLsi = FidelityContext.UniqueName("sim2real-behav-plain-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = withoutLsi,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH), new KeySchemaElement("sk", KeyType.RANGE)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S), new AttributeDefinition("sk", ScalarAttributeType.N)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real", Value = CheckId }],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(withoutLsi));

        await ddb.WaitForTableExists(withLsi, delaySeconds: 2, maxAttempts: 250);
        await ddb.WaitForTableExists(withoutLsi, delaySeconds: 2, maxAttempts: 250);

        var reported = await ddb.PutItemAsync(new PutItemRequest
        {
            TableName = withLsi,
            Item = Item(("pk", S("coll")), ("sk", N("1")), ("li", S("x"))),
            ReturnItemCollectionMetrics = ReturnItemCollectionMetrics.SIZE,
        });
        var metrics = reported.ItemCollectionMetrics;
        Ensure(
            metrics is not null && (metrics.ItemCollectionKey is not null || metrics.SizeEstimateRangeGB is not null),
            $"a write to an LSI-bearing table must report item-collection metrics, got {Py.Repr(Repr(metrics))}");
        Ensure(
            Py.Equal(metrics.ItemCollectionKey, Item(("pk", S("coll")))),
            $"the collection key must be the partition key alone, got {Py.Repr(metrics.ItemCollectionKey)}");
        var estimate = metrics.SizeEstimateRangeGB;
        Ensure(estimate is { Count: 2 }, $"SizeEstimateRangeGB must be a two-element lower/upper bound, got {Py.Repr(estimate)}");
        Ensure(estimate[0] <= estimate[1], $"the estimate range must be ordered, got {Py.Repr(estimate)}");

        var plain = await ddb.PutItemAsync(new PutItemRequest
        {
            TableName = withoutLsi,
            Item = Item(("pk", S("coll")), ("sk", N("1"))),
            ReturnItemCollectionMetrics = ReturnItemCollectionMetrics.SIZE,
        });
        Ensure(
            plain.ItemCollectionMetrics is null,
            $"a table with no LSI has no item collection, so nothing may be reported, got {Py.Repr(Repr(plain.ItemCollectionMetrics))}");

        // Not requesting the metrics suppresses them even where a collection exists,
        // so the field tracks the request parameter and not merely the table shape.
        var unrequested = await ddb.PutItemAsync(new PutItemRequest
        {
            TableName = withLsi,
            Item = Item(("pk", S("coll")), ("sk", N("2")), ("li", S("y"))),
        });
        Ensure(
            unrequested.ItemCollectionMetrics is null,
            $"item-collection metrics must be absent when not requested, got {Py.Repr(Repr(unrequested.ItemCollectionMetrics))}");
    }
}
