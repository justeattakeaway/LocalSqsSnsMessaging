// Translated from behavioral/dynamodb/check_gsi_scan_filter_expression_parenthesized_or_under_and.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Ddb;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.gsi-scan-filter-expression-parenthesized-or-under-and</summary>
[Category("AwsEvaluationSuite")]
public sealed class GsiScanFilterExpressionParenthesizedOrUnderAnd
{
    private const string CheckId = "gsi-scan-filter-expression-parenthesized-or-under-and";
    private const string Index = "category-index";

    /// <summary>
    /// Six records as (id, category, status, flagged). Every expected set is derived from these at
    /// run time. item-005 is the control: status=new with flagged=False, so the parenthesized grouping
    /// excludes it (the AND on #f applies to the whole OR group) but the unparenthesized precedence
    /// `#s = :s1 OR (#s = :s2 AND #f = :yes)` matches it on the OR leg alone.
    /// </summary>
    private static readonly (string Id, string Category, string Status, bool Flagged)[] Rows =
    [
        ("item-001", "books", "new", true),
        ("item-002", "books", "used", true),
        ("item-003", "toys", "new", true),
        ("item-004", "toys", "used", false),
        ("item-005", "games", "new", false),
        ("item-006", "games", "used", false),
    ];

    private static Dictionary<string, AttributeValue> RowItem((string Id, string Category, string Status, bool Flagged) row) =>
        Item(("id", S(row.Id)), ("category", S(row.Category)), ("status", S(row.Status)), ("flagged", Bool(row.Flagged)));

    /// <summary>A fresh id-keyed table, with a category GSI, seeded with the six records.</summary>
    private static async Task<string> Seed(FidelityContext ctx)
    {
        var ddb = ctx.Dynamo;
        var name = FidelityContext.UniqueName("sim2real-ddbgsiparen-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = name,
            Tags = [new Tag { Key = "sim2real", Value = CheckId }],
            KeySchema = [new KeySchemaElement("id", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("id", ScalarAttributeType.S), new AttributeDefinition("category", ScalarAttributeType.S)],
            GlobalSecondaryIndexes =
            [
                new GlobalSecondaryIndex
                {
                    IndexName = Index,
                    KeySchema = [new KeySchemaElement("category", KeyType.HASH)],
                    Projection = new Projection { ProjectionType = ProjectionType.ALL },
                },
            ],
            BillingMode = BillingMode.PAY_PER_REQUEST,
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(name));
        await ddb.WaitForTableExists(name, delaySeconds: 2, maxAttempts: 250);
        var resp = await ddb.BatchWriteItemAsync(new BatchWriteItemRequest
        {
            RequestItems = new Dictionary<string, List<WriteRequest>>(StringComparer.Ordinal)
            {
                [name] = [.. Rows.Select(r => new WriteRequest { PutRequest = new PutRequest { Item = RowItem(r) } })],
            },
        });
        Ensure((resp.UnprocessedItems ?? []).Count == 0, $"seeding BatchWriteItem left {Py.Repr(resp.UnprocessedItems)} unprocessed");
        await WaitIndexActive(ddb, name);
        await WaitIndexCaughtUp(ddb, name);
        return name;
    }

    /// <summary>Poll the table description until the GSI reaches ACTIVE.</summary>
    private static async Task WaitIndexActive(IAmazonDynamoDB ddb, string name)
    {
        var clock = FidelityTime.StartNew();
        while (true)
        {
            var gsis = (await ddb.DescribeTableAsync(name)).Table.GlobalSecondaryIndexes ?? [];
            var statuses = gsis.ToDictionary(g => g.IndexName, g => g.IndexStatus.Value, StringComparer.Ordinal);
            if (statuses.GetValueOrDefault(Index) == "ACTIVE")
            {
                return;
            }

            Ensure(clock.Elapsed < TimeSpan.FromSeconds(180), $"index {Py.Repr(Index)} did not reach ACTIVE: {Py.Repr(statuses)}");
            await FidelityTime.Delay(TimeSpan.FromSeconds(2));
        }
    }

    /// <summary>Poll the index scan's ScannedCount until replication has carried over every seed row.</summary>
    private static async Task WaitIndexCaughtUp(IAmazonDynamoDB ddb, string name)
    {
        var clock = FidelityTime.StartNew();
        while (true)
        {
            var scanned = (await ddb.ScanAsync(new ScanRequest { TableName = name, IndexName = Index, Select = Select.COUNT })).ScannedCount;
            if (scanned == Rows.Length)
            {
                return;
            }

            Ensure(clock.Elapsed < TimeSpan.FromSeconds(60), $"index {Py.Repr(Index)} ScannedCount stuck at {Py.Repr(scanned)}, expected {Py.Repr(Rows.Length)}");
            await FidelityTime.Delay(TimeSpan.FromSeconds(1));
        }
    }

    private static List<string> Ids(IEnumerable<Dictionary<string, AttributeValue>> items) =>
        [.. items.Select(i => i["id"].S).Order(StringComparer.Ordinal)];

    /// <summary>Every page of a Scan over the category index; (ids, Count sum, ScannedCount sum).</summary>
    private static async Task<(List<string> Ids, int Count, int Scanned)> IndexScan(IAmazonDynamoDB ddb, string name, string filterExpr)
    {
        var items = new List<Dictionary<string, AttributeValue>>();
        var count = 0;
        var scanned = 0;
        Dictionary<string, AttributeValue>? start = null;
        while (true)
        {
            var page = await ddb.ScanAsync(new ScanRequest
            {
                TableName = name,
                IndexName = Index,
                FilterExpression = filterExpr,
                ExpressionAttributeNames = Names(("#s", "status"), ("#f", "flagged")),
                ExpressionAttributeValues = Item((":s1", S("new")), (":s2", S("used")), (":yes", Bool(true))),
                ExclusiveStartKey = start is { Count: > 0 } ? start : null,
            });
            items.AddRange(page.Items);
            count += page.Count ?? 0;
            scanned += page.ScannedCount ?? 0;
            start = page.LastEvaluatedKey;
            if (start is null || start.Count == 0)
            {
                return (Ids(items), count, scanned);
            }
        }
    }

    /// <summary>
    /// A GSI Scan's FilterExpression honors an explicit `(#s = :s1 OR #s = :s2) AND #f = :yes`
    /// grouping, which selects a different set than the unparenthesized OR/AND precedence.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.gsi-scan-filter-expression-parenthesized-or-under-and")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var name = await Seed(ctx);

        var (groupedIds, groupedCount, groupedScanned) = await IndexScan(ddb, name, "(#s = :s1 OR #s = :s2) AND #f = :yes");
        var expected = Rows.Where(r => r.Status is "new" or "used" && r.Flagged).Select(r => r.Id).Order(StringComparer.Ordinal).ToList();
        Ensure(
            groupedIds.SequenceEqual(expected),
            $"'(#s = :s1 OR #s = :s2) AND #f = :yes' on the category index returned {Py.Repr(groupedIds)}, expected {Py.Repr(expected)}; the unparenthesized precedence '#s = :s1 OR (#s = :s2 AND #f = :yes)' would also include item-006");
        Ensure(
            (groupedCount, groupedScanned) == (expected.Count, Rows.Length),
            $"the grouped index scan reported Count {Py.Repr(groupedCount)} / ScannedCount {Py.Repr(groupedScanned)}, expected {expected.Count} / {Rows.Length} (every row sits in the index, since category is never absent)");

        var (unparenthesizedIds, _, _) = await IndexScan(ddb, name, "#s = :s1 OR #s = :s2 AND #f = :yes");
        var expectedLoose = Rows.Where(r => r.Status == "new" || (r.Status == "used" && r.Flagged)).Select(r => r.Id).Order(StringComparer.Ordinal).ToList();
        Ensure(
            unparenthesizedIds.SequenceEqual(expectedLoose),
            $"'#s = :s1 OR #s = :s2 AND #f = :yes' (AND binds tighter, no parens) returned {Py.Repr(unparenthesizedIds)}, expected {Py.Repr(expectedLoose)}");
        Ensure(
            !unparenthesizedIds.SequenceEqual(groupedIds),
            $"the parenthesized and unparenthesized forms returned the same set {Py.Repr(groupedIds)}; the seed cannot tell the two parses apart");
    }
}
