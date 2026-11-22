// Translated from behavioral/dynamodb/check_scan_filter_size_counts_utf16_code_units_not_bytes.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit abd0811).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.scan-filter-size-counts-utf16-code-units-not-bytes</summary>
[Category("AwsEvaluationSuite")]
public sealed class ScanFilterSizeCountsUtf16CodeUnitsNotBytes
{
    private const string Check = "scan-filter-size-counts-utf16-code-units-not-bytes";

    // pk -> (string value, its UTF-16 code unit count). "bmp" is 3 multi-byte BMP characters (7
    // UTF-8 bytes, 3 code units); "astral" is one single code point outside the BMP, which UTF-16
    // represents as a surrogate pair (4 UTF-8 bytes, 1 code point, 2 code units); "ascii2" is a
    // plain two-character string that lands on the same code-unit count as "astral" so a filter
    // that matched on byte or code-point count instead of code units would pick the wrong row.
    private const string Bmp = "éα中";
    private const string Astral = "\U0001F600";

    private static readonly (string Pk, string Value)[] Rows =
    [
        ("bmp", Bmp),
        ("astral", Astral),
        ("ascii2", "hi"),
    ];

    /// <summary>
    /// A Scan FilterExpression's size() on a String counts UTF-16 code units, so a surrogate-pair
    /// character counts as 2 even though it is one code point and 4 UTF-8 bytes.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.scan-filter-size-counts-utf16-code-units-not-bytes")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-ddbu16-");
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

        foreach (var (pk, value) in Rows)
        {
            await ddb.PutItemAsync(table, Item(("pk", S(pk)), ("s", S(value))));
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

        var three = await ScanPks("size(s) = :n", Item((":n", N("3"))));
        Ensure(three.SetEquals(["bmp"]), $"size(s) = 3 selected {Py.Repr(three)}, expected ['bmp']");

        var two = await ScanPks("size(s) = :n", Item((":n", N("2"))));
        Ensure(
            two.SetEquals(["astral", "ascii2"]),
            $"size(s) = 2 selected {Py.Repr(two)}, expected ['ascii2', 'astral'] " +
            "(the astral character's UTF-16 surrogate pair, same count as the two-character string)");

        // Neither the 7 UTF-8 bytes of "bmp" nor the single code point of "astral" is what size()
        // reports: both comparisons below select nothing.
        var seven = await ScanPks("size(s) = :n", Item((":n", N("7"))));
        Ensure(seven.Count == 0, $"size(s) = 7 selected {Py.Repr(seven)}, expected none (not UTF-8 bytes)");

        var one = await ScanPks("size(s) = :n", Item((":n", N("1"))));
        Ensure(one.Count == 0, $"size(s) = 1 selected {Py.Repr(one)}, expected none (not code points)");

        // Positive control: comparing the attribute directly (no size()) still finds the row,
        // proving the table and ExpressionAttributeValues plumbing work on their own.
        var control = await ScanPks("s = :v", Item((":v", S(Bmp))));
        Ensure(control.SetEquals(["bmp"]), $"s = <the bmp string> selected {Py.Repr(control)}, expected ['bmp']");
    }
}
