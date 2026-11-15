// Translated from behavioral/dynamodb/check_scan_filter_size_covers_s_b_ss_l_m_not_n.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Ddb;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.scan-filter-size-covers-s-b-ss-l-m-not-n</summary>
[Category("AwsEvaluationSuite")]
public sealed class ScanFilterSizeCoversSBSsLMNotN
{
    private const string Check = "scan-filter-size-covers-s-b-ss-l-m-not-n";

    /// <summary>
    /// pk -> item. "match" and "other" share every attribute name but differ in size for each
    /// type, so a filter that selects by the wrong metric (bytes vs characters, a nested value
    /// instead of a count) would pick the wrong row or both.
    /// </summary>
    private static (string Pk, Dictionary<string, AttributeValue> Attrs)[] Items() =>
    [
        ("match", Item(
            ("s", S("abc")), ("b", B("XY")), ("ss", SS("p", "q", "r")),
            ("l", L(N("1"), N("2"))),
            ("m", M(("x", N("1")), ("y", N("2")))),
            ("n", N("7")))),
        ("other", Item(
            ("s", S("abcdef")), ("b", B("XYZAB")), ("ss", SS("p", "q", "r", "s")),
            ("l", L(N("1"), N("2"), N("3"))),
            ("m", M(("x", N("1")), ("y", N("2")), ("z", N("3")))),
            ("n", N("42")))),
    ];

    /// <summary>(attribute, size of "match"'s value) -- the size every type's filter below selects on.</summary>
    private static readonly (string Attr, int Size)[] Sizes = [("s", 3), ("b", 2), ("ss", 3), ("l", 2), ("m", 2)];

    /// <summary>
    /// A Scan FilterExpression's size() selects by a String's characters, a Binary's bytes, a
    /// Set's or List's element count and a Map's key count -- in parenthesized form too -- and on a
    /// Number it always evaluates false rather than the number's own value.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.scan-filter-size-covers-s-b-ss-l-m-not-n")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-ddbscnsz-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            Tags = [new Tag { Key = "sim2real", Value = Check }],
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(table));
        await ddb.WaitForTableExists(table, delaySeconds: 2, maxAttempts: 250);
        foreach (var (pk, attrs) in Items())
        {
            var item = Item(("pk", S(pk)));
            foreach (var (k, v) in attrs)
            {
                item[k] = v;
            }

            await ddb.PutItemAsync(table, item);
        }

        async Task<SortedSet<string>> ScanPks(string expr, Dictionary<string, AttributeValue> values)
        {
            var resp = await ddb.ScanAsync(new ScanRequest
            {
                TableName = table,
                ConsistentRead = true,
                FilterExpression = expr,
                ExpressionAttributeValues = values,
            });
            return new SortedSet<string>(resp.Items.Select(i => i["pk"].S), StringComparer.Ordinal);
        }

        static string Str(int n) => n.ToString(CultureInfo.InvariantCulture);

        foreach (var (attr, n) in Sizes)
        {
            var got = await ScanPks($"size({attr}) = :n", Item((":n", N(Str(n)))));
            Ensure(got.SetEquals(["match"]), $"size({attr}) = {n} selected {Py.Repr(got)}, expected ['match']");
        }

        // The parenthesized form around the whole function call parses and evaluates identically.
        foreach (var (attr, n) in Sizes)
        {
            var got = await ScanPks($"(size({attr})) = :n", Item((":n", N(Str(n)))));
            Ensure(got.SetEquals(["match"]), $"(size({attr})) = {n} selected {Py.Repr(got)}, expected ['match']");
        }

        // size() on a Number is unsupported: the comparison evaluates false for every item, even
        // against the number's own stored value -- never an error, and never a numeric answer.
        var emptyGt = await ScanPks("size(n) > :zero", Item((":zero", N("0"))));
        Ensure(emptyGt.Count == 0, $"size(n) > 0 selected {Py.Repr(emptyGt)}, expected none");
        var emptyEq = await ScanPks("size(n) = :seven", Item((":seven", N("7"))));
        Ensure(
            emptyEq.Count == 0,
            $"size(n) = 7 selected {Py.Repr(emptyEq)} even though 'match'.n is 7; size() must not fall back to the number's own value");

        // Positive control: comparing n directly (no size()) still selects exactly the matching item,
        // proving the table and the comparator work and that size() alone is what excludes N.
        var control = await ScanPks("n = :seven", Item((":seven", N("7"))));
        Ensure(control.SetEquals(["match"]), $"n = 7 selected {Py.Repr(control)}, expected ['match']");
    }
}
