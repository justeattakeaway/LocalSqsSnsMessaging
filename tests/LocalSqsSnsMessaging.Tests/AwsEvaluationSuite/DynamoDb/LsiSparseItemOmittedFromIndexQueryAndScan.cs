// Translated from behavioral/dynamodb/check_lsi_sparse_item_omitted_from_index_query_and_scan.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Ddb;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.lsi-sparse-item-omitted-from-index-query-and-scan</summary>
[Category("AwsEvaluationSuite")]
public sealed class LsiSparseItemOmittedFromIndexQueryAndScan
{
    private const string CheckId = "lsi-sparse-item-omitted-from-index-query-and-scan";
    private static readonly string[] One = ["1"];
    private static readonly string[] Both = ["1", "2"];

    private static List<string> Sks(List<Dictionary<string, AttributeValue>> items) => [.. items.Select(i => i["sk"].S).Order(StringComparer.Ordinal)];

    /// <summary>
    /// An item with no LSI sort key is left out of an LSI Query and an LSI Scan, while the base
    /// table's Query and Scan still return it.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.lsi-sparse-item-omitted-from-index-query-and-scan")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH), new KeySchemaElement("sk", KeyType.RANGE)],
            AttributeDefinitions =
            [
                new AttributeDefinition("pk", ScalarAttributeType.S),
                new AttributeDefinition("sk", ScalarAttributeType.S),
                new AttributeDefinition("lsk", ScalarAttributeType.N),
            ],
            LocalSecondaryIndexes =
            [
                new LocalSecondaryIndex
                {
                    IndexName = "lsi1",
                    KeySchema = [new KeySchemaElement("pk", KeyType.HASH), new KeySchemaElement("lsk", KeyType.RANGE)],
                    Projection = new Projection { ProjectionType = ProjectionType.ALL },
                },
            ],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real", Value = CheckId }],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(table));
        await ddb.WaitForTableExists(table, delaySeconds: 2, maxAttempts: 250);

        await ddb.PutItemAsync(table, Item(("pk", S("item")), ("sk", S("1")), ("lsk", N("1"))));
        await ddb.PutItemAsync(table, Item(("pk", S("item")), ("sk", S("2"))));

        var lsiQuery = await ddb.QueryAsync(new QueryRequest
        {
            TableName = table,
            IndexName = "lsi1",
            KeyConditionExpression = "pk = :pk",
            ExpressionAttributeValues = Item((":pk", S("item"))),
            ConsistentRead = true,
        });
        var lsiQuerySks = Sks(lsiQuery.Items);
        Ensure(lsiQuerySks.SequenceEqual(One), $"the item without lsk must be left out of the LSI Query, got sk values {Py.Repr(lsiQuerySks)}");

        var lsiScan = await ddb.ScanAsync(new ScanRequest { TableName = table, IndexName = "lsi1", ConsistentRead = true });
        var lsiScanSks = Sks(lsiScan.Items);
        Ensure(lsiScanSks.SequenceEqual(One), $"the item without lsk must be left out of the LSI Scan, got sk values {Py.Repr(lsiScanSks)}");

        var baseQuery = await ddb.QueryAsync(new QueryRequest
        {
            TableName = table,
            KeyConditionExpression = "pk = :pk",
            ExpressionAttributeValues = Item((":pk", S("item"))),
            ConsistentRead = true,
        });
        var baseQuerySks = Sks(baseQuery.Items);
        Ensure(baseQuerySks.SequenceEqual(Both), $"the base table Query must still return both items, got sk values {Py.Repr(baseQuerySks)}");

        var baseScan = await ddb.ScanAsync(new ScanRequest { TableName = table, ConsistentRead = true });
        var baseScanSks = Sks(baseScan.Items);
        Ensure(baseScanSks.SequenceEqual(Both), $"the base table Scan must still return both items, got sk values {Py.Repr(baseScanSks)}");
    }
}
