// Translated from behavioral/dynamodb/check_scan_1mb_page_is_the_minimal_byte_prefix_crossing_the_limit.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit abd0811).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.scan-1mb-page-is-the-minimal-byte-prefix-crossing-the-limit</summary>
[Category("AwsEvaluationSuite")]
public sealed class Scan1mbPageIsTheMinimalBytePrefixCrossingTheLimit
{
    private const string Check = "scan-1mb-page-is-the-minimal-byte-prefix-crossing-the-limit";
    private const int OneMb = 1024 * 1024;

    // A single partition (one hash key, many sort keys) scans in a deterministic order, so the exact
    // split point is reproducible: each item is sized so that neither adding nor dropping one can land
    // the running total exactly on the boundary.
    private const string Pk = "shard";
    private const int PerItem = 87_400;
    private const int NameBytes = 2 + 5 + 2 + 3 + 1;
    private static readonly string Payload = new('x', PerItem - NameBytes);

    private static Func<Task> Teardown(Func<Task> undo, params string[] notFound) => async () =>
    {
        var error = await FidelityContext.CaptureError(undo);
        if (error is not null && !notFound.Contains(error.Code))
        {
            throw error.Exception;
        }
    };

    private static Dictionary<string, AttributeValue> MakeItem(int i) =>
        Item(("pk", S(Pk)), ("sk", S($"{i:D3}")), ("v", S(Payload)));

    /// <summary>Item size for an all-string item: UTF-8 length of each name plus its value.</summary>
    private static int ItemBytes(Dictionary<string, AttributeValue> item) =>
        item.Sum(e => Encoding.UTF8.GetByteCount(e.Key) + Encoding.UTF8.GetByteCount(e.Value.S));

    private static async Task WaitActive(IAmazonDynamoDB ddb, string table)
    {
        var clock = FidelityTime.StartNew();
        while (clock.Elapsed < TimeSpan.FromSeconds(270.0))
        {
            if ((await ddb.DescribeTableAsync(table)).Table.TableStatus == TableStatus.ACTIVE)
            {
                return;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(2));
        }

        throw Fail($"table {Py.Repr(table)} did not become ACTIVE before the deadline");
    }

    /// <summary>
    /// A truncated Scan page is the shortest prefix of the remaining items exceeding 1 MB: it
    /// includes the item that crosses the boundary, and dropping that one item would not.
    /// </summary>
    private static void AssertMinimalPrefix(ScanResponse resp, string label)
    {
        var items = resp.Items;
        var total = items.Sum(ItemBytes);
        var withoutLast = total - ItemBytes(items[^1]);
        Ensure(
            total > OneMb,
            $"{label}: a page carrying LastEvaluatedKey was truncated by the 1 MB budget, so its bytes must exceed it; got {total} bytes over {items.Count} items");
        Ensure(
            withoutLast <= OneMb,
            $"{label}: dropping the page's last item must bring it back under 1 MB -- the page must be the SHORTEST prefix exceeding the budget; without the last item it is still {withoutLast} bytes");
    }

    private static string Summary(ScanResponse resp) =>
        $"Count={Py.Repr(resp.Count)} ScannedCount={Py.Repr(resp.ScannedCount)} LastEvaluatedKey={Py.Repr(resp.LastEvaluatedKey)}";

    /// <summary>
    /// A no-Limit Scan page is the shortest item prefix exceeding 1 MB -- it includes the item
    /// that crosses the boundary, Count equals ScannedCount with no filter, and a strongly consistent
    /// scan costs exactly twice the eventually consistent one at that same boundary.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.scan-1mb-page-is-the-minimal-byte-prefix-crossing-the-limit")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-ddbscan1mb-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH), new KeySchemaElement("sk", KeyType.RANGE)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S), new AttributeDefinition("sk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real", Value = Check }],
        });
        ctx.OnCleanup(Teardown(() => ddb.DeleteTableAsync(table), "ResourceNotFoundException"));
        await WaitActive(ddb, table);

        async Task<(ScanResponse Resp, double? Units)> Units(bool consistent)
        {
            var resp = await ddb.ScanAsync(new ScanRequest { TableName = table, ConsistentRead = consistent, ReturnConsumedCapacity = ReturnConsumedCapacity.TOTAL });
            return (resp, resp.ConsumedCapacity.CapacityUnits);
        }

        // Phase 1: 11 items sit just under the 1 MB budget -- one more of this size would cross it.
        for (var i = 0; i < 11; i++)
        {
            await ddb.PutItemAsync(table, MakeItem(i));
        }

        var underTotal = 11 * PerItem;
        Ensure(
            underTotal < OneMb && underTotal + PerItem > OneMb,
            $"the under-boundary fixture must sit within one item of the limit; got {Py.Repr(underTotal)}");
        var (resp1, strong1) = await Units(consistent: true);
        Ensure(
            resp1.LastEvaluatedKey is null,
            $"11 items totalling {underTotal} bytes (under 1 MB) must fit on one page without truncation, got LastEvaluatedKey {Py.Repr(resp1.LastEvaluatedKey)}");
        Ensure(
            resp1.Count == resp1.ScannedCount && resp1.ScannedCount == 11 && resp1.Items.Count == 11,
            $"with no FilterExpression Count must equal ScannedCount and the item count; got Count={Py.Repr(resp1.Count)} ScannedCount={Py.Repr(resp1.ScannedCount)} items={Py.Repr(resp1.Items.Count)}");
        var page1Bytes = resp1.Items.Sum(ItemBytes);
        Ensure(
            page1Bytes == underTotal,
            $"the under-boundary page must return exactly the 11 seeded items, totalling {underTotal} bytes; got {page1Bytes} bytes");
        var (_, eventual1) = await Units(consistent: false);
        Ensure(
            strong1 == 2 * eventual1 && strong1 > 0,
            $"a strongly consistent scan must cost exactly twice the eventually consistent one; got strong={Py.Repr(strong1)} eventual={Py.Repr(eventual1)}");

        // Phase 2: one more item makes the whole table (now 12 items) cross the budget by less than
        // one item's width -- AWS still hands back a LastEvaluatedKey even though nothing is left.
        await ddb.PutItemAsync(table, MakeItem(11));
        var (resp2, _) = await Units(consistent: true);
        Ensure(
            resp2.LastEvaluatedKey is not null,
            $"12 items whose total exceeds 1 MB must truncate and carry a LastEvaluatedKey even though they are the entire table; got {Summary(resp2)}");
        Ensure(
            resp2.Count == resp2.ScannedCount && resp2.ScannedCount == 12,
            $"Count={Py.Repr(resp2.Count)} ScannedCount={Py.Repr(resp2.ScannedCount)}, expected 12 and 12");
        AssertMinimalPrefix(resp2, "phase 2 (12-item table, single page)");
        var resume2 = await ddb.ScanAsync(new ScanRequest { TableName = table, ConsistentRead = true, ExclusiveStartKey = resp2.LastEvaluatedKey });
        Ensure(
            resume2.Count == resume2.ScannedCount && resume2.ScannedCount == 0 && resume2.LastEvaluatedKey is null,
            $"resuming from that LastEvaluatedKey must find the table truly exhausted; got Count={Py.Repr(resume2.Count)} LastEvaluatedKey={Py.Repr(resume2.LastEvaluatedKey)}");

        // Phase 3: a 13th item now sits beyond the boundary, so the first page still stops at the
        // shortest prefix exceeding 1 MB and the 13th item is left for a second page.
        await ddb.PutItemAsync(table, MakeItem(12));
        var (resp3, _) = await Units(consistent: true);
        Ensure(resp3.LastEvaluatedKey is not null, $"13 items must still truncate on the first page; got {Summary(resp3)}");
        AssertMinimalPrefix(resp3, "phase 3 (13-item table, page 1)");
        var resume3 = await ddb.ScanAsync(new ScanRequest { TableName = table, ConsistentRead = true, ExclusiveStartKey = resp3.LastEvaluatedKey });
        Ensure(
            resume3.LastEvaluatedKey is null,
            $"the second page must be the terminal one; got LastEvaluatedKey {Py.Repr(resume3.LastEvaluatedKey)}");
        var firstKeys = resp3.Items.Select(it => it["sk"].S).ToHashSet(StringComparer.Ordinal);
        var secondKeys = resume3.Items.Select(it => it["sk"].S).ToHashSet(StringComparer.Ordinal);
        var shared = firstKeys.Intersect(secondKeys, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        Ensure(shared.Count == 0, $"the two pages must not overlap; shared keys {Py.Repr(shared)}");
        var union = firstKeys.Union(secondKeys, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        Ensure(
            union.SequenceEqual(Enumerable.Range(0, 13).Select(i => $"{i:D3}")),
            $"the two pages together must cover all 13 items exactly once; got {Py.Repr(union)}");
        Ensure(
            resp3.Count + resume3.Count == 13,
            $"Count across both pages must sum to 13; got {Py.Repr(resp3.Count)} + {Py.Repr(resume3.Count)}");
    }
}
