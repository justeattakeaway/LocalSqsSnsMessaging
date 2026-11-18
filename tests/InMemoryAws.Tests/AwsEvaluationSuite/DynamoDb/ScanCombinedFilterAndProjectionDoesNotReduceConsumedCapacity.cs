// Translated from behavioral/dynamodb/check_scan_combined_filter_and_projection_does_not_reduce_consumed_capacity.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.scan-combined-filter-and-projection-does-not-reduce-consumed-capacity</summary>
[Category("AwsEvaluationSuite")]
public sealed class ScanCombinedFilterAndProjectionDoesNotReduceConsumedCapacity
{
    private const string Check = "scan-combined-filter-and-projection-does-not-reduce-consumed-capacity";

    /// <summary>Five items, each its own partition; only two carry tag "match".</summary>
    private static readonly (string Pk, string Tag)[] Rows =
    [
        ("i0", "match"),
        ("i1", "other"),
        ("i2", "match"),
        ("i3", "other"),
        ("i4", "other"),
    ];

    private static Dictionary<string, AttributeValue> RowItem((string Pk, string Tag) row) => Item(("pk", S(row.Pk)), ("tag", S(row.Tag)));

    /// <summary>
    /// A Scan's ConsumedCapacity with a FilterExpression and a ProjectionExpression together
    /// equals the unfiltered, unprojected scan: both act only after the item is read and billed.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.scan-combined-filter-and-projection-does-not-reduce-consumed-capacity")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var t = FidelityContext.UniqueName("sim2real-behav-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = t,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real", Value = Check }],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(t));
        await ddb.WaitForTableExists(t, delaySeconds: 2, maxAttempts: 250);
        foreach (var row in Rows)
        {
            await ddb.PutItemAsync(t, RowItem(row));
        }

        var baseline = await ddb.ScanAsync(new ScanRequest { TableName = t, ConsistentRead = true, ReturnConsumedCapacity = ReturnConsumedCapacity.TOTAL });
        Ensure(
            baseline.Count == Rows.Length && baseline.ScannedCount == Rows.Length,
            $"the unfiltered scan must see every seeded item, got Count={Py.Repr(baseline.Count)} ScannedCount={Py.Repr(baseline.ScannedCount)} over {Rows.Length} seeded");
        var combined = await ddb.ScanAsync(new ScanRequest
        {
            TableName = t,
            ConsistentRead = true,
            ReturnConsumedCapacity = ReturnConsumedCapacity.TOTAL,
            FilterExpression = "#tg = :m",
            ProjectionExpression = "#pk",
            ExpressionAttributeNames = Names(("#tg", "tag"), ("#pk", "pk")),
            ExpressionAttributeValues = Item((":m", S("match"))),
        });
        Ensure(combined.Count == 2, $"the combined filter+projection scan matched {Py.Repr(combined.Count)} items, expected 2");
        Ensure(
            combined.ScannedCount == Rows.Length,
            $"a filtered scan must still scan the whole table, got ScannedCount={Py.Repr(combined.ScannedCount)}, expected {Rows.Length}");
        foreach (var item in combined.Items)
        {
            Ensure(
                item.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(["pk"]),
                $"the projected item had keys {Py.Repr(item.Keys.Order(StringComparer.Ordinal).ToList())}, expected only 'pk'");
        }

        var baselineCu = baseline.ConsumedCapacity.CapacityUnits;
        var combinedCu = combined.ConsumedCapacity.CapacityUnits;
        Ensure(
            combinedCu == baselineCu,
            $"a scan combining FilterExpression and ProjectionExpression billed {Py.Repr(combinedCu)}, the unfiltered unprojected scan billed {Py.Repr(baselineCu)} -- neither must reduce the cost");
    }
}
