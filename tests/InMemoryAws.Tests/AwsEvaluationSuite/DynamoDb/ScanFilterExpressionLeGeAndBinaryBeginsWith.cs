// Translated from behavioral/dynamodb/check_scan_filter_expression_le_ge_and_binary_begins_with.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.scan-filter-expression-le-ge-and-binary-begins-with</summary>
[Category("AwsEvaluationSuite")]
public sealed class ScanFilterExpressionLeGeAndBinaryBeginsWith
{
    private const string Check = "scan-filter-expression-le-ge-and-binary-begins-with";

    /// <summary>
    /// Five seeded items: a string name, a number score and a binary blob. Every expected set below
    /// is derived from these at run time.
    /// </summary>
    private static readonly (string Id, string Name, string Score, byte[] Blob)[] Rows =
    [
        ("id1", "apple", "10", [0x01, 0xaa]),
        ("id2", "banana", "20", [0x01, 0xbb]),
        ("id3", "cherry", "30", [0x02, 0xaa]),
        ("id4", "date", "40", [0x02, 0xbb]),
        ("id5", "eggplant", "50", [0x03, 0xaa]),
    ];

    /// <summary>Python's repr of a set of strings.</summary>
    private static string SetRepr(IEnumerable<string> values) =>
        values.Any() ? "{" + string.Join(", ", values.Order(StringComparer.Ordinal).Select(v => Py.Repr(v))) + "}" : "set()";

    private static HashSet<string> Set(IEnumerable<string> values) => values.ToHashSet(StringComparer.Ordinal);

    /// <summary>
    /// A Scan FilterExpression's &lt;= on an S and an N attribute, >= on an N attribute, and
    /// begins_with() on a B attribute each match the legacy ScanFilter twin's set exactly.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.scan-filter-expression-le-ge-and-binary-begins-with")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-scanle-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            KeySchema = [new KeySchemaElement("id", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("id", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real", Value = Check }],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(table));
        await ddb.WaitForTableExists(table, delaySeconds: 2, maxAttempts: 250);
        foreach (var (itemId, name, score, blob) in Rows)
        {
            await ddb.PutItemAsync(table, Item(("id", S(itemId)), ("name", S(name)), ("score", N(score)), ("blob", B(blob))));
        }

        async Task<HashSet<string>> ScanIds(
            string? filterExpression = null,
            Dictionary<string, string>? names = null,
            Dictionary<string, AttributeValue>? values = null,
            Dictionary<string, Condition>? scanFilter = null)
        {
            var ids = Set([]);
            Dictionary<string, AttributeValue>? start = null;
            while (true)
            {
                var page = await ddb.ScanAsync(new ScanRequest
                {
                    TableName = table,
                    ConsistentRead = true,
                    FilterExpression = filterExpression,
                    ExpressionAttributeNames = names,
                    ExpressionAttributeValues = values,
                    ScanFilter = scanFilter,
                    ExclusiveStartKey = start,
                });
                ids.UnionWith(page.Items.Select(item => item["id"].S));
                start = page.LastEvaluatedKey;
                if (start is null || start.Count == 0)
                {
                    return ids;
                }
            }
        }

        static Dictionary<string, Condition> Legacy(string attribute, ComparisonOperator op, AttributeValue value) =>
            new(StringComparer.Ordinal) { [attribute] = new Condition { ComparisonOperator = op, AttributeValueList = [value] } };

        // <= on an S attribute: name <= 'cherry' selects every name at or before it in byte order.
        var nameLe = Set(Rows.Where(r => ((ReadOnlySpan<byte>)Encoding.UTF8.GetBytes(r.Name)).SequenceCompareTo("cherry"u8) <= 0).Select(r => r.Id));
        var feNameLe = await ScanIds("#n <= :v", Names(("#n", "name")), Item((":v", S("cherry"))));
        Ensure(feNameLe.SetEquals(nameLe), $"FilterExpression 'name <= cherry' returned {SetRepr(feNameLe)}, expected {SetRepr(nameLe)}");
        var legacyNameLe = await ScanIds(scanFilter: Legacy("name", ComparisonOperator.LE, S("cherry")));
        Ensure(
            legacyNameLe.SetEquals(feNameLe),
            $"legacy ScanFilter LE on name returned {SetRepr(legacyNameLe)}, must match FilterExpression's {SetRepr(feNameLe)}");

        // <= on an N attribute: score <= 30 selects the three lowest scores, compared numerically.
        var scoreLe = Set(Rows.Where(r => int.Parse(r.Score, CultureInfo.InvariantCulture) <= 30).Select(r => r.Id));
        var feScoreLe = await ScanIds("score <= :v", values: Item((":v", N("30"))));
        Ensure(feScoreLe.SetEquals(scoreLe), $"FilterExpression 'score <= 30' returned {SetRepr(feScoreLe)}, expected {SetRepr(scoreLe)}");
        var legacyScoreLe = await ScanIds(scanFilter: Legacy("score", ComparisonOperator.LE, N("30")));
        Ensure(
            legacyScoreLe.SetEquals(feScoreLe),
            $"legacy ScanFilter LE on score returned {SetRepr(legacyScoreLe)}, must match FilterExpression's {SetRepr(feScoreLe)}");

        // >= on an N attribute: score >= 30 selects the three highest scores.
        var scoreGe = Set(Rows.Where(r => int.Parse(r.Score, CultureInfo.InvariantCulture) >= 30).Select(r => r.Id));
        var feScoreGe = await ScanIds("score >= :v", values: Item((":v", N("30"))));
        Ensure(feScoreGe.SetEquals(scoreGe), $"FilterExpression 'score >= 30' returned {SetRepr(feScoreGe)}, expected {SetRepr(scoreGe)}");
        var legacyScoreGe = await ScanIds(scanFilter: Legacy("score", ComparisonOperator.GE, N("30")));
        Ensure(
            legacyScoreGe.SetEquals(feScoreGe),
            $"legacy ScanFilter GE on score returned {SetRepr(legacyScoreGe)}, must match FilterExpression's {SetRepr(feScoreGe)}");

        // begins_with() on a B attribute: blob starting with the single byte 0x01.
        var blobBw = Set(Rows.Where(r => r.Blob[0] == 0x01).Select(r => r.Id));
        var feBlobBw = await ScanIds("begins_with(#b, :v)", Names(("#b", "blob")), Item((":v", B([0x01]))));
        Ensure(feBlobBw.SetEquals(blobBw), $"FilterExpression 'begins_with(blob, 0x01)' returned {SetRepr(feBlobBw)}, expected {SetRepr(blobBw)}");
        var legacyBlobBw = await ScanIds(scanFilter: Legacy("blob", ComparisonOperator.BEGINS_WITH, B([0x01])));
        Ensure(
            legacyBlobBw.SetEquals(feBlobBw),
            $"legacy ScanFilter BEGINS_WITH on blob returned {SetRepr(legacyBlobBw)}, must match FilterExpression's {SetRepr(feBlobBw)}");
    }
}
