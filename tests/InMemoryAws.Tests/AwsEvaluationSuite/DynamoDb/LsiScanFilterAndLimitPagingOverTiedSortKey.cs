// Translated from behavioral/dynamodb/check_lsi_scan_filter_and_limit_paging_over_tied_sort_key.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit abd0811).
using System.Globalization;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.lsi-scan-filter-and-limit-paging-over-tied-sort-key</summary>
[Category("AwsEvaluationSuite")]
public sealed class LsiScanFilterAndLimitPagingOverTiedSortKey
{
    private const string Check = "lsi-scan-filter-and-limit-paging-over-tied-sort-key";
    private const string Index = "lsi1";
    private const int Limit = 10;
    private static readonly string[] LekAttributes = ["pk", "sk", "lsk"];

    /// <summary>
    /// 20 items in one partition, five distinct LSI sort-key values each shared by four items
    /// (ties), category alternating within each tied group so the filter cannot follow sort order.
    /// </summary>
    private static readonly List<Dictionary<string, AttributeValue>> Items = [.. Enumerable.Range(0, 20).Select(i => Item(
        ("pk", S("p0")),
        ("sk", S("s" + i.ToString("00", CultureInfo.InvariantCulture))),
        ("lsk", N((i / 4).ToString(CultureInfo.InvariantCulture))),
        ("category", S(i % 2 == 0 ? "even" : "odd"))))];

    private static readonly List<string> EvenKeys =
        [.. Items.Where(it => it["category"].S == "even").Select(it => it["sk"].S).Order(StringComparer.Ordinal)];

    /// <summary>Every page of a Scan, following LastEvaluatedKey; returns (items, per-page ScannedCount, LEKs).</summary>
    private static async Task<(List<Dictionary<string, AttributeValue>> Items, List<int> ScannedCounts, List<Dictionary<string, AttributeValue>> Leks)> ScanAllPages(IAmazonDynamoDB ddb, ScanRequest request)
    {
        var items = new List<Dictionary<string, AttributeValue>>();
        var scannedCounts = new List<int>();
        var leks = new List<Dictionary<string, AttributeValue>>();
        while (true)
        {
            var page = await ddb.ScanAsync(request);
            items.AddRange(page.Items ?? []);
            scannedCounts.Add(page.ScannedCount ?? 0);
            var lek = page.LastEvaluatedKey;
            if (lek is null)
            {
                return (items, scannedCounts, leks);
            }

            leks.Add(lek);
            Ensure(leks.Count <= 40, $"LSI scan paging did not end after 40 pages: {Py.Repr(leks)}");
            request.ExclusiveStartKey = lek;
        }
    }

    /// <summary>
    /// A Limit=10 FilterExpression Scan of an LSI over a partition tied on the index sort key
    /// returns every matching item exactly once, paging on ScannedCount rather than match count.
    ///
    /// Reference: https://docs.aws.amazon.com/amazondynamodb/latest/developerguide/Query.html
    /// </summary>
    [Test]
    [DisplayName("dynamodb.lsi-scan-filter-and-limit-paging-over-tied-sort-key")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-ddb-lsifilim-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            Tags = [new Tag { Key = "sim2real", Value = Check }],
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
                    IndexName = Index,
                    KeySchema = [new KeySchemaElement("pk", KeyType.HASH), new KeySchemaElement("lsk", KeyType.RANGE)],
                    Projection = new Projection { ProjectionType = ProjectionType.ALL },
                },
            ],
            BillingMode = BillingMode.PAY_PER_REQUEST,
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(table));
        await ddb.WaitForTableExists(table, delaySeconds: 2, maxAttempts: 250);

        foreach (var item in Items)
        {
            await ddb.PutItemAsync(table, item);
        }

        var (unfiltered, unfilteredScanned, unfilteredLeks) = await ScanAllPages(ddb, new ScanRequest
        {
            TableName = table,
            IndexName = Index,
            ConsistentRead = true,
            Limit = Limit,
        });
        var allKeys = Items.Select(it => it["sk"].S).Order(StringComparer.Ordinal).ToList();
        var unfilteredKeys = unfiltered.Select(it => it["sk"].S).Order(StringComparer.Ordinal).ToList();
        Ensure(
            unfilteredKeys.SequenceEqual(allKeys) && unfiltered.Count == Items.Count,
            $"unfiltered Limit={Limit} LSI scan returned {TupleRepr(unfilteredKeys)}, expected every written item exactly once");
        Ensure(
            unfilteredScanned.SequenceEqual([Limit, Limit, 0]) && unfilteredLeks.Count == 2,
            $"unfiltered Limit={Limit} LSI scan of {Items.Count} items paged ScannedCounts {Py.Repr(unfilteredScanned)} with {unfilteredLeks.Count} LastEvaluatedKey(s), expected [{Limit}, {Limit}, 0] with 2");

        var (filtered, filteredScanned, filteredLeks) = await ScanAllPages(ddb, new ScanRequest
        {
            TableName = table,
            IndexName = Index,
            ConsistentRead = true,
            Limit = Limit,
            FilterExpression = "category = :c",
            ExpressionAttributeValues = Item((":c", S("even"))),
        });
        var gotKeys = filtered.Select(it => it["sk"].S).Order(StringComparer.Ordinal).ToList();
        Ensure(
            gotKeys.SequenceEqual(EvenKeys) && filtered.Count == EvenKeys.Count,
            $"filtered Limit={Limit} LSI scan over tied sort keys returned {TupleRepr(gotKeys)}, expected {TupleRepr(EvenKeys)}");
        foreach (var it in filtered)
        {
            Ensure(Py.Equal(it["category"], S("even")), $"a non-matching item leaked through the filter: {Py.Repr(it)}");
        }

        Ensure(
            filteredScanned.SequenceEqual([Limit, Limit, 0]) && filteredLeks.Count == 2,
            $"filtered Limit={Limit} LSI scan paged ScannedCounts {Py.Repr(filteredScanned)} with {filteredLeks.Count} LastEvaluatedKey(s), expected [{Limit}, {Limit}, 0] with 2: Limit counts examined items, not matches");
        foreach (var lek in filteredLeks)
        {
            Ensure(lek.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(LekAttributes), $"LastEvaluatedKey {Py.Repr(lek)} must name exactly pk, sk and lsk");
        }
    }

    /// <summary><c>repr(tuple(values))</c>.</summary>
    private static string TupleRepr(List<string> values) =>
        values.Count == 1 ? $"({Py.Repr(values[0])},)" : "(" + string.Join(", ", values.Select(v => Py.Repr(v))) + ")";
}
