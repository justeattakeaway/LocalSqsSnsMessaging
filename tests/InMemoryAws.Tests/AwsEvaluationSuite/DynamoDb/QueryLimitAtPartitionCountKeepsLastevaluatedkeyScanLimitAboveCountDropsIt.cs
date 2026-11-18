// Translated from behavioral/dynamodb/check_query_limit_at_partition_count_keeps_lastevaluatedkey_scan_limit_above_count_drops_it.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.query-limit-at-partition-count-keeps-lastevaluatedkey-scan-limit-above-count-drops-it</summary>
[Category("AwsEvaluationSuite")]
public sealed class QueryLimitAtPartitionCountKeepsLastevaluatedkeyScanLimitAboveCountDropsIt
{
    private const string Check = "query-limit-at-partition-count-keeps-lastevaluatedkey-scan-limit-above-count-drops-it";

    /// <summary>
    /// Query with Limit equal to its partition's item count still returns a LastEvaluatedKey,
    /// whose next page is empty with none; Scan with Limit above the table's item count returns
    /// all items with no LastEvaluatedKey.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.query-limit-at-partition-count-keeps-lastevaluatedkey-scan-limit-above-count-drops-it")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var t = FidelityContext.UniqueName("sim2real-behav-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = t,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH), new KeySchemaElement("sk", KeyType.RANGE)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S), new AttributeDefinition("sk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real", Value = Check }],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(t));
        await ddb.WaitForTableExists(t, delaySeconds: 2, maxAttempts: 250);
        List<string> written = ["001", "002", "003"];
        foreach (var sk in written)
        {
            await ddb.PutItemAsync(t, Item(("pk", S("p1")), ("sk", S(sk))));
        }

        var first = await ddb.QueryAsync(new QueryRequest
        {
            TableName = t,
            KeyConditionExpression = "pk = :pk",
            ExpressionAttributeValues = Item((":pk", S("p1"))),
            Limit = 3,
            ConsistentRead = true,
        });
        Ensure(first.Count == 3, $"expected Query Limit=3 over a 3-item partition to return Count 3, got {Py.Repr(first.Count)}");
        var firstSks = first.Items.Select(i => i["sk"].S).ToList();
        Ensure(firstSks.SequenceEqual(written), $"expected items in ascending sort-key order {Py.Repr(written)}, got {Py.Repr(firstSks)}");
        Ensure(first.LastEvaluatedKey is not null, "Limit exactly equal to the partition's item count must still return a LastEvaluatedKey");
        Ensure(
            Py.Equal(first.LastEvaluatedKey, Item(("pk", S("p1")), ("sk", S("003")))),
            $"expected LastEvaluatedKey to name the last evaluated item (sk 003), got {Py.Repr(first.LastEvaluatedKey)}");
        var second = await ddb.QueryAsync(new QueryRequest
        {
            TableName = t,
            KeyConditionExpression = "pk = :pk",
            ExpressionAttributeValues = Item((":pk", S("p1"))),
            Limit = 3,
            ExclusiveStartKey = first.LastEvaluatedKey,
            ConsistentRead = true,
        });
        Ensure(second.Count == 0, $"expected the page after the boundary LastEvaluatedKey to be empty, got Count {Py.Repr(second.Count)}");
        Ensure(second.Items.Count == 0, $"expected no items past the boundary, got {Py.Repr(second.Items)}");
        Ensure(second.LastEvaluatedKey is null, $"expected no LastEvaluatedKey on the final empty page, got {Py.Repr(second.LastEvaluatedKey)}");
        var scanned = await ddb.ScanAsync(new ScanRequest { TableName = t, Limit = 5, ConsistentRead = true });
        Ensure(scanned.Count == 3, $"expected Scan Limit=5 over a 3-item table to return Count 3, got {Py.Repr(scanned.Count)}");
        Ensure(scanned.ScannedCount == 3, $"expected unfiltered ScannedCount to equal Count 3, got {Py.Repr(scanned.ScannedCount)}");
        var scannedSks = new SortedSet<string>(scanned.Items.Select(i => i["sk"].S), StringComparer.Ordinal);
        Ensure(scannedSks.SetEquals(written), $"expected Scan to return every written item, got {Py.Repr(scannedSks)}");
        Ensure(
            scanned.LastEvaluatedKey is null,
            $"expected no LastEvaluatedKey when Limit exceeds the table's item count, got {Py.Repr(scanned.LastEvaluatedKey)}");
    }
}
