// Translated from behavioral/dynamodb/check_scan_filter_legacy_set_equality_eq_ne_on_ss_ns_bs.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit abd0811).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.scan-filter-legacy-set-equality-eq-ne-on-ss-ns-bs</summary>
[Category("AwsEvaluationSuite")]
public sealed class ScanFilterLegacySetEqualityEqNeOnSsNsBs
{
    private const string Check = "scan-filter-legacy-set-equality-eq-ne-on-ss-ns-bs";

    private static byte[] Bin(string text) => Encoding.UTF8.GetBytes(text);

    private static async Task<SortedSet<string>> ScanSks(IAmazonDynamoDB ddb, string table, string attr, Condition filt)
    {
        var resp = await ddb.ScanAsync(new ScanRequest
        {
            TableName = table,
            ScanFilter = new Dictionary<string, Condition>(StringComparer.Ordinal) { [attr] = filt },
            ConsistentRead = true,
        });
        return new SortedSet<string>(resp.Items.Select(item => item["sk"].S), StringComparer.Ordinal);
    }

    /// <summary>
    /// Legacy ScanFilter EQ on SS/NS/BS matches only the item whose set holds exactly the
    /// same members regardless of order; NE matches every other item, including one missing it.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.scan-filter-legacy-set-equality-eq-ne-on-ss-ns-bs")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-legsetfilt-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH), new KeySchemaElement("sk", KeyType.RANGE)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S), new AttributeDefinition("sk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real", Value = Check }],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(table));
        await ddb.WaitForTableExists(table, delaySeconds: 2, maxAttempts: 250);

        const string pk = "p1";
        var rows = new (string Sk, (string Name, AttributeValue Value)[] Attrs)[]
        {
            ("exact_ss", [("ssattr", SS("b", "a"))]),
            ("subset_ss", [("ssattr", SS("a"))]),
            ("superset_ss", [("ssattr", SS("a", "b", "c"))]),
            ("exact_ns", [("nsattr", NS("2", "1"))]),
            ("subset_ns", [("nsattr", NS("1"))]),
            ("superset_ns", [("nsattr", NS("1", "2", "3"))]),
            ("exact_bs", [("bsattr", BS(Bin("y"), Bin("x")))]),
            ("subset_bs", [("bsattr", BS(Bin("x")))]),
            ("superset_bs", [("bsattr", BS(Bin("x"), Bin("y"), Bin("z")))]),
            ("missing_all", []),
        };
        foreach (var (sk, attrs) in rows)
        {
            var item = Item(("pk", S(pk)), ("sk", S(sk)));
            foreach (var (name, value) in attrs)
            {
                item[name] = value;
            }

            await ddb.PutItemAsync(table, item);
        }

        var allSks = new SortedSet<string>(rows.Select(r => r.Sk), StringComparer.Ordinal);

        var cases = new (string Attr, Func<AttributeValue> EqValue, string ExactSk)[]
        {
            ("ssattr", () => SS("a", "b"), "exact_ss"),
            ("nsattr", () => NS("1", "2"), "exact_ns"),
            ("bsattr", () => BS(Bin("x"), Bin("y")), "exact_bs"),
        };

        foreach (var (attr, eqValue, exactSk) in cases)
        {
            var eqMatched = await ScanSks(ddb, table, attr, new Condition { ComparisonOperator = ComparisonOperator.EQ, AttributeValueList = [eqValue()] });
            Ensure(
                eqMatched.SetEquals([exactSk]),
                $"ScanFilter {attr} EQ matched {Py.Repr(eqMatched)}, expected only {{{Py.Repr(exactSk)}}} " +
                "(member order, a subset and a superset must not match)");

            var neMatched = await ScanSks(ddb, table, attr, new Condition { ComparisonOperator = ComparisonOperator.NE, AttributeValueList = [eqValue()] });
            var complement = new SortedSet<string>(allSks.Where(s => s != exactSk), StringComparer.Ordinal);
            Ensure(
                neMatched.SetEquals(complement),
                $"ScanFilter {attr} NE matched {Py.Repr(neMatched)}, expected the exact complement " +
                $"{Py.Repr(complement)} (including the item missing {Py.Repr(attr)})");
        }
    }
}
