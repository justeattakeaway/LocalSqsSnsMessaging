// Translated from behavioral/dynamodb/check_legacy_scan_query_filter_comparator_type_mismatch.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Ddb;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.legacy-scan-query-filter-comparator-type-mismatch</summary>
[Category("AwsEvaluationSuite")]
public sealed class LegacyScanQueryFilterComparatorTypeMismatch
{
    private const string CheckId = "legacy-scan-query-filter-comparator-type-mismatch";

    // A legacy ScanFilter/QueryFilter ComparisonOperator condition whose AttributeValueList
    // element is a different type than the stored attribute excludes every item for these
    // comparators; NE includes every item instead, because the type mismatch alone satisfies
    // "not equal".
    private static readonly string[] ExcludesEveryItem = ["EQ", "LE", "LT", "GE", "GT"];
    private static readonly string[] Both = ["other", "target"];
    private static readonly string[] TargetOnly = ["target"];

    private static Condition Filter(string op, params AttributeValue[] values) => new() { ComparisonOperator = op, AttributeValueList = [.. values] };

    /// <summary>
    /// A legacy ScanFilter/QueryFilter comparator whose value type differs from the stored
    /// attribute's matches no item, except NE, which then matches every item.
    ///
    /// Reference: https://docs.aws.amazon.com/amazondynamodb/latest/developerguide/LegacyConditionalParameters.Conditions.html
    /// </summary>
    [Test]
    [DisplayName("dynamodb.legacy-scan-query-filter-comparator-type-mismatch")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-ddbtmf-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH), new KeySchemaElement("sk", KeyType.RANGE)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S), new AttributeDefinition("sk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real", Value = CheckId }],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(table));
        await ddb.WaitForTableExists(table, delaySeconds: 1, maxAttempts: 60);

        await ddb.PutItemAsync(table, Item(("pk", S("p")), ("sk", S("target")), ("num", N("6"))));
        await ddb.PutItemAsync(table, Item(("pk", S("p")), ("sk", S("other")), ("num", N("99"))));

        async Task<List<string>> ScanIds(Func<Condition> filt)
        {
            var resp = await ddb.ScanAsync(new ScanRequest
            {
                TableName = table,
                ScanFilter = new Dictionary<string, Condition>(StringComparer.Ordinal) { ["num"] = filt() },
                ConsistentRead = true,
            });
            return [.. resp.Items.Select(item => item["sk"].S).Order(StringComparer.Ordinal)];
        }

        async Task<List<string>> QueryIds(Func<Condition> filt)
        {
            var resp = await ddb.QueryAsync(new QueryRequest
            {
                TableName = table,
                KeyConditions = new Dictionary<string, Condition>(StringComparer.Ordinal)
                {
                    ["pk"] = new Condition { AttributeValueList = [S("p")], ComparisonOperator = ComparisonOperator.EQ },
                },
                QueryFilter = new Dictionary<string, Condition>(StringComparer.Ordinal) { ["num"] = filt() },
                ConsistentRead = true,
            });
            return [.. resp.Items.Select(item => item["sk"].S).Order(StringComparer.Ordinal)];
        }

        foreach (var op in ExcludesEveryItem)
        {
            Condition Filt() => Filter(op, S("6"));
            var scannedOp = await ScanIds(Filt);
            Ensure(scannedOp.Count == 0, $"ScanFilter {op} with a type-mismatched value matched {Py.Repr(scannedOp)}, expected none");
            var queriedOp = await QueryIds(Filt);
            Ensure(queriedOp.Count == 0, $"QueryFilter {op} with a type-mismatched value matched {Py.Repr(queriedOp)}, expected none");
        }

        Condition BetweenFilt() => Filter("BETWEEN", S("0"), S("9"));
        var scanned = await ScanIds(BetweenFilt);
        Ensure(scanned.Count == 0, $"ScanFilter BETWEEN with type-mismatched bounds matched {Py.Repr(scanned)}, expected none");
        var queried = await QueryIds(BetweenFilt);
        Ensure(queried.Count == 0, $"QueryFilter BETWEEN with type-mismatched bounds matched {Py.Repr(queried)}, expected none");

        // NE: every item's type differs from the filter value's, so every item is "not equal"
        // and matches, including the item whose own value equals 6 numerically.
        Condition NeFilt() => Filter("NE", S("6"));
        scanned = await ScanIds(NeFilt);
        Ensure(scanned.SequenceEqual(Both), $"ScanFilter NE with a type-mismatched value matched {Py.Repr(scanned)}, expected both items");
        queried = await QueryIds(NeFilt);
        Ensure(queried.SequenceEqual(Both), $"QueryFilter NE with a type-mismatched value matched {Py.Repr(queried)}, expected both items");

        // A number compared against a number set of the same member follows the same rule.
        scanned = await ScanIds(() => Filter("EQ", NS("6")));
        Ensure(scanned.Count == 0, $"ScanFilter EQ N-vs-NS matched {Py.Repr(scanned)}, expected none");
        scanned = await ScanIds(() => Filter("NE", NS("6")));
        Ensure(scanned.SequenceEqual(Both), $"ScanFilter NE N-vs-NS matched {Py.Repr(scanned)}, expected both items");

        // Positive control: the filtering mechanism itself discriminates on value, not just on
        // type -- a matched-type EQ returns only the item whose number actually equals 6.
        Condition MatchedFilt() => Filter("EQ", N("6"));
        scanned = await ScanIds(MatchedFilt);
        Ensure(scanned.SequenceEqual(TargetOnly), $"positive control: ScanFilter EQ with a matched type returned {Py.Repr(scanned)}");
        queried = await QueryIds(MatchedFilt);
        Ensure(queried.SequenceEqual(TargetOnly), $"positive control: QueryFilter EQ with a matched type returned {Py.Repr(queried)}");
    }
}
