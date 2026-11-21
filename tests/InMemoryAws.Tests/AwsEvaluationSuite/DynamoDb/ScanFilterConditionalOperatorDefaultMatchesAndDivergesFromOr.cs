// Translated from behavioral/dynamodb/check_scan_filter_conditional_operator_default_matches_and_diverges_from_or.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit abd0811).
using System.Globalization;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.scan-filter-conditional-operator-default-matches-and-diverges-from-or</summary>
[Category("AwsEvaluationSuite")]
public sealed class ScanFilterConditionalOperatorDefaultMatchesAndDivergesFromOr
{
    private const string Check = "scan-filter-conditional-operator-default-matches-and-diverges-from-or";

    /// <summary>
    /// Five rows where the GE-only and EQ-only ScanFilter conditions overlap on exactly
    /// one row, so their AND and OR compositions resolve to different, countable sets.
    /// </summary>
    private static async Task Seed(IAmazonDynamoDB ddb, string table)
    {
        (string Sku, int Price, string Tag)[] rows =
        [
            ("sku-widget", 50, "clearance"),
            ("sku-gadget", 150, "overstock"),
            ("sku-gizmo", 200, "clearance"),
            ("sku-doodad", 30, "overstock"),
            ("sku-trinket", 120, "seasonal"),
        ];
        foreach (var (sku, price, tag) in rows)
        {
            await ddb.PutItemAsync(table, Item(("sku", S(sku)), ("price", N(price.ToString(CultureInfo.InvariantCulture))), ("tag", S(tag))));
        }
    }

    private static List<string> Sorted(IEnumerable<string> values) => [.. values.Order(StringComparer.Ordinal)];

    /// <summary>
    /// Two legacy ScanFilter conditions are ANDed when ConditionalOperator is omitted,
    /// the same set as ConditionalOperator=AND, and a larger, different set under OR.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.scan-filter-conditional-operator-default-matches-and-diverges-from-or")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-scfand-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            KeySchema = [new KeySchemaElement("sku", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("sku", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real", Value = Check }],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(table));
        await ddb.WaitForTableExists(table, delaySeconds: 2, maxAttempts: 250);
        await Seed(ddb, table);

        Condition PriceCond() => new() { ComparisonOperator = ComparisonOperator.GE, AttributeValueList = [N("100")] };
        Condition TagCond() => new() { ComparisonOperator = ComparisonOperator.EQ, AttributeValueList = [S("clearance")] };

        async Task<HashSet<string>> ScanSkus(Dictionary<string, Condition> scanFilter, ConditionalOperator? conditionalOperator = null)
        {
            var resp = await ddb.ScanAsync(new ScanRequest
            {
                TableName = table,
                ConsistentRead = true,
                ScanFilter = scanFilter,
                ConditionalOperator = conditionalOperator,
            });
            return resp.Items.Select(item => item["sku"].S).ToHashSet(StringComparer.Ordinal);
        }

        var priceOnly = await ScanSkus(new() { ["price"] = PriceCond() });
        Ensure(
            priceOnly.SetEquals(["sku-gadget", "sku-gizmo", "sku-trinket"]),
            $"price GE 100 alone matched {Py.Repr(Sorted(priceOnly))}, expected the three priced at or above 100");

        var tagOnly = await ScanSkus(new() { ["tag"] = TagCond() });
        Ensure(
            tagOnly.SetEquals(["sku-widget", "sku-gizmo"]),
            $"tag EQ clearance alone matched {Py.Repr(Sorted(tagOnly))}, expected the two tagged clearance");

        Dictionary<string, Condition> BothFilters() => new() { ["price"] = PriceCond(), ["tag"] = TagCond() };
        string[] expectedAnd = ["sku-gizmo"];
        string[] expectedOr = ["sku-widget", "sku-gadget", "sku-gizmo", "sku-trinket"];

        var omitted = await ScanSkus(BothFilters());
        Ensure(
            omitted.SetEquals(expectedAnd),
            $"omitting ConditionalOperator matched {Py.Repr(Sorted(omitted))}, expected the AND intersection {Py.Repr(Sorted(expectedAnd))}");

        var explicitAnd = await ScanSkus(BothFilters(), ConditionalOperator.AND);
        Ensure(
            explicitAnd.SetEquals(expectedAnd),
            $"ConditionalOperator=AND matched {Py.Repr(Sorted(explicitAnd))}, expected the same intersection {Py.Repr(Sorted(expectedAnd))} the omitted form produced");

        var explicitOr = await ScanSkus(BothFilters(), ConditionalOperator.OR);
        Ensure(
            explicitOr.SetEquals(expectedOr),
            $"ConditionalOperator=OR matched {Py.Repr(Sorted(explicitOr))}, expected the union {Py.Repr(Sorted(expectedOr))}");
        Ensure(
            explicitOr.Count > expectedAnd.Length,
            $"the OR union {Py.Repr(Sorted(explicitOr))} is not larger than the AND intersection {Py.Repr(Sorted(expectedAnd))}");
    }
}
