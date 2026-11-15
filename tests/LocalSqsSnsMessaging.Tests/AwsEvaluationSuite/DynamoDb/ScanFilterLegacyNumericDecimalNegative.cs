// Translated from behavioral/dynamodb/check_scan_filter_legacy_numeric_decimal_negative.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Ddb;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.scan-filter-legacy-numeric-decimal-negative</summary>
[Category("AwsEvaluationSuite")]
public sealed class ScanFilterLegacyNumericDecimalNegative
{
    private const string Check = "scan-filter-legacy-numeric-decimal-negative";

    /// <summary>
    /// Five rows with a decimal and negative N column <c>amount</c>. Every expected set is derived
    /// from these numeric values, compared numerically rather than as strings.
    /// </summary>
    private static readonly (string Id, string Amount)[] Rows =
    [
        ("r1", "-5"),
        ("r2", "-1.5"),
        ("r3", "0.25"),
        ("r4", "3.75"),
        ("r5", "10"),
    ];

    private static Dictionary<string, Condition> Amount(ComparisonOperator op, params string[] values) =>
        new(StringComparer.Ordinal) { ["amount"] = new Condition { AttributeValueList = [.. values.Select(N)], ComparisonOperator = op } };

    /// <summary>
    /// Legacy ScanFilter LE/LT/GE/GT/BETWEEN on an N attribute compare decimals and negatives
    /// numerically: -1.5 &lt; 0.25 &lt; 10.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.scan-filter-legacy-numeric-decimal-negative")]
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

        var le = await ScanIds(Amount(ComparisonOperator.LE, "0.25"));
        Ensure(le.SequenceEqual(["r1", "r2", "r3"]), $"LE 0.25 returned {Py.Repr(le)}, expected ['r1', 'r2', 'r3'] (amount<=0.25: -5, -1.5, 0.25)");
        var lt = await ScanIds(Amount(ComparisonOperator.LT, "0.25"));
        Ensure(lt.SequenceEqual(["r1", "r2"]), $"LT 0.25 returned {Py.Repr(lt)}, expected ['r1', 'r2'] (amount<0.25: -5, -1.5)");
        var ge = await ScanIds(Amount(ComparisonOperator.GE, "3.75"));
        Ensure(ge.SequenceEqual(["r4", "r5"]), $"GE 3.75 returned {Py.Repr(ge)}, expected ['r4', 'r5'] (amount>=3.75: 3.75, 10)");
        var gt = await ScanIds(Amount(ComparisonOperator.GT, "0.25"));
        Ensure(gt.SequenceEqual(["r4", "r5"]), $"GT 0.25 returned {Py.Repr(gt)}, expected ['r4', 'r5'] (amount>0.25: 3.75, 10)");
        var between = await ScanIds(Amount(ComparisonOperator.BETWEEN, "-1.5", "3.75"));
        Ensure(
            between.SequenceEqual(["r2", "r3", "r4"]),
            $"BETWEEN -1.5 and 3.75 returned {Py.Repr(between)}, expected ['r2', 'r3', 'r4'] (inclusive: -1.5, 0.25, 3.75)");
    }

    /// <summary>A fresh table under a minted name, seeded with the five rows via one BatchWriteItem.</summary>
    private static async Task<string> Seed(FidelityContext ctx)
    {
        var ddb = ctx.Dynamo;
        var name = FidelityContext.UniqueName("sim2real-ddbnd-");
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
        var items = Rows.Select(r => new WriteRequest { PutRequest = new PutRequest { Item = Item(("id", S(r.Id)), ("amount", N(r.Amount))) } }).ToList();
        var resp = await ddb.BatchWriteItemAsync(new BatchWriteItemRequest { RequestItems = new() { [name] = items } });
        Ensure((resp.UnprocessedItems ?? []).Count == 0, $"seeding BatchWriteItem left {Py.Repr(resp.UnprocessedItems)} unprocessed");
        return name;
    }
}
