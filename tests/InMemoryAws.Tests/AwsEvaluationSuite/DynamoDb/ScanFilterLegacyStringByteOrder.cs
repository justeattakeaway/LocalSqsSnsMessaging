// Translated from behavioral/dynamodb/check_scan_filter_legacy_string_byte_order.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.scan-filter-legacy-string-byte-order</summary>
[Category("AwsEvaluationSuite")]
public sealed class ScanFilterLegacyStringByteOrder
{
    private const string Check = "scan-filter-legacy-string-byte-order";

    /// <summary>
    /// Five rows: a byte-order column <c>s</c> (A=0x41, B=0x42, Z=0x5A, a=0x61, ab=0x61,0x62) and an
    /// unrelated <c>tag</c> column for the CONTAINS case. Every expected set is derived from these.
    /// </summary>
    private static readonly (string Id, string S, string Tag)[] Rows =
    [
        ("r1", "A", "apple"),
        ("r2", "B", "banana"),
        ("r3", "Z", "cherry"),
        ("r4", "a", "date"),
        ("r5", "ab", "elderberry"),
    ];

    private static Dictionary<string, Condition> Filter(string attribute, ComparisonOperator op, params string[] values) =>
        new(StringComparer.Ordinal) { [attribute] = new Condition { AttributeValueList = [.. values.Select(Ddb.S)], ComparisonOperator = op } };

    /// <summary>
    /// Legacy ScanFilter LE/LT/GE/GT/BETWEEN on an S attribute compare by UTF-8 byte order
    /// (lowercase sorts above uppercase, so "a" > "B"), and CONTAINS matches a substring.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.scan-filter-legacy-string-byte-order")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var name = await Seed(ctx);

        async Task<List<string>> ScanIds(Dictionary<string, Condition> scanFilter)
        {
            var resp = await ddb.ScanAsync(new ScanRequest { TableName = name, ScanFilter = scanFilter, ConsistentRead = true });
            return [.. resp.Items.Select(i => i["id"].S).Order(StringComparer.Ordinal)];
        }

        var le = await ScanIds(Filter("s", ComparisonOperator.LE, "B"));
        Ensure(le.SequenceEqual(["r1", "r2"]), $"LE 'B' returned {Py.Repr(le)}, expected ['r1', 'r2'] (s<='B': A, B)");
        var lt = await ScanIds(Filter("s", ComparisonOperator.LT, "B"));
        Ensure(lt.SequenceEqual(["r1"]), $"LT 'B' returned {Py.Repr(lt)}, expected ['r1'] (s<'B': A only)");
        var ge = await ScanIds(Filter("s", ComparisonOperator.GE, "Z"));
        Ensure(
            ge.SequenceEqual(["r3", "r4", "r5"]),
            $"GE 'Z' returned {Py.Repr(ge)}, expected ['r3', 'r4', 'r5'] (s>='Z': Z, a, ab; lowercase sorts above uppercase)");
        var gt = await ScanIds(Filter("s", ComparisonOperator.GT, "B"));
        Ensure(gt.SequenceEqual(["r3", "r4", "r5"]), $"GT 'B' returned {Py.Repr(gt)}, expected ['r3', 'r4', 'r5'] (s>'B': Z, a, ab)");
        var between = await ScanIds(Filter("s", ComparisonOperator.BETWEEN, "B", "Z"));
        Ensure(
            between.SequenceEqual(["r2", "r3"]),
            $"BETWEEN 'B' and 'Z' returned {Py.Repr(between)}, expected ['r2', 'r3'] (inclusive: B, Z; 'a' and 'ab' sort above 'Z' and are excluded)");
        var contains = await ScanIds(Filter("tag", ComparisonOperator.CONTAINS, "an"));
        Ensure(
            contains.SequenceEqual(["r2"]),
            $"CONTAINS 'an' on tag returned {Py.Repr(contains)}, expected ['r2'] ('banana' is the only tag holding the substring 'an')");
    }

    /// <summary>A fresh table under a minted name, seeded with the five rows via one BatchWriteItem.</summary>
    private static async Task<string> Seed(FidelityContext ctx)
    {
        var ddb = ctx.Dynamo;
        var name = FidelityContext.UniqueName("sim2real-ddbsbo-");
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
        var items = Rows.Select(r => new WriteRequest { PutRequest = new PutRequest { Item = Item(("id", S(r.Id)), ("s", S(r.S)), ("tag", S(r.Tag))) } }).ToList();
        var resp = await ddb.BatchWriteItemAsync(new BatchWriteItemRequest { RequestItems = new() { [name] = items } });
        Ensure((resp.UnprocessedItems ?? []).Count == 0, $"seeding BatchWriteItem left {Py.Repr(resp.UnprocessedItems)} unprocessed");
        return name;
    }
}
