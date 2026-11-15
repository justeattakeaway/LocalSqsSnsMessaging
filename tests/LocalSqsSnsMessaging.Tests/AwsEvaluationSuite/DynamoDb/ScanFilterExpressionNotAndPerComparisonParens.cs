// Translated from behavioral/dynamodb/check_scan_filter_expression_not_and_per_comparison_parens.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Ddb;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.scan-filter-expression-not-and-per-comparison-parens</summary>
[Category("AwsEvaluationSuite")]
public sealed class ScanFilterExpressionNotAndPerComparisonParens
{
    private const string Check = "scan-filter-expression-not-and-per-comparison-parens";

    /// <summary>Five records as (id, status, category). Every expected set is derived from these at run time.</summary>
    private static readonly (string Id, string Status, string Category)[] Rows =
    [
        ("item-001", "active", "premium"),
        ("item-002", "inactive", "basic"),
        ("item-003", "pending", "basic"),
        ("item-004", "active", "basic"),
        ("item-005", "suspended", "premium"),
    ];

    private static Dictionary<string, AttributeValue> RowItem((string Id, string Status, string Category) row) =>
        Item(("id", S(row.Id)), ("status", S(row.Status)), ("category", S(row.Category)));

    /// <summary>A fresh id-keyed table seeded with the five records in one BatchWriteItem.</summary>
    private static async Task<string> Seed(FidelityContext ctx)
    {
        var ddb = ctx.Dynamo;
        var name = FidelityContext.UniqueName("sim2real-ddbparen-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = name,
            Tags = [new Tag { Key = "sim2real", Value = Check }],
            KeySchema = [new KeySchemaElement("id", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("id", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(name));
        await ddb.WaitForTableExists(name, delaySeconds: 2, maxAttempts: 250);
        var resp = await ddb.BatchWriteItemAsync(new BatchWriteItemRequest
        {
            RequestItems = new() { [name] = [.. Rows.Select(r => new WriteRequest { PutRequest = new PutRequest { Item = RowItem(r) } })] },
        });
        Ensure((resp.UnprocessedItems ?? []).Count == 0, $"seeding BatchWriteItem left {Py.Repr(resp.UnprocessedItems)} unprocessed");
        return name;
    }

    private static List<string> Ids(IEnumerable<Dictionary<string, AttributeValue>> items) => [.. items.Select(i => i["id"].S).Order(StringComparer.Ordinal)];

    private static async Task<List<Dictionary<string, AttributeValue>>> Scan(IAmazonDynamoDB ddb, string name, string filterExpr)
    {
        var items = new List<Dictionary<string, AttributeValue>>();
        Dictionary<string, AttributeValue>? start = null;
        while (true)
        {
            var page = await ddb.ScanAsync(new ScanRequest
            {
                TableName = name,
                FilterExpression = filterExpr,
                ConsistentRead = true,
                ExpressionAttributeNames = Names(("#s", "status"), ("#c", "category")),
                ExpressionAttributeValues = Item((":a", S("active")), (":b", S("basic"))),
                ExclusiveStartKey = start,
            });
            items.AddRange(page.Items);
            start = page.LastEvaluatedKey;
            if (start is null || start.Count == 0)
            {
                return items;
            }
        }
    }

    /// <summary>
    /// A Scan FilterExpression groups a NOT comparison in its own parentheses, and groups
    /// each AND operand in its own parentheses, each matching its unparenthesized equivalent.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.scan-filter-expression-not-and-per-comparison-parens")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var name = await Seed(ctx);
        var notInGroup = Ids(await Scan(ddb, name, "(NOT #s = :a) AND #c = :b"));
        List<string> expectedTight = [.. Rows.Where(r => r.Status != "active" && r.Category == "basic").Select(r => r.Id).Order(StringComparer.Ordinal)];
        Ensure(notInGroup.SequenceEqual(expectedTight), $"'(NOT #s = :a) AND #c = :b' returned {Py.Repr(notInGroup)}, expected {Py.Repr(expectedTight)}");
        var notInGroupBothGrouped = Ids(await Scan(ddb, name, "(NOT #s = :a) AND (#c = :b)"));
        Ensure(
            notInGroupBothGrouped.SequenceEqual(expectedTight),
            $"'(NOT #s = :a) AND (#c = :b)' returned {Py.Repr(notInGroupBothGrouped)}, expected {Py.Repr(expectedTight)}");
        var perComparison = Ids(await Scan(ddb, name, "(#s = :a) AND (#c = :b)"));
        List<string> expectedAnd = [.. Rows.Where(r => r.Status == "active" && r.Category == "basic").Select(r => r.Id).Order(StringComparer.Ordinal)];
        Ensure(perComparison.SequenceEqual(expectedAnd), $"'(#s = :a) AND (#c = :b)' returned {Py.Repr(perComparison)}, expected {Py.Repr(expectedAnd)}");
    }
}
