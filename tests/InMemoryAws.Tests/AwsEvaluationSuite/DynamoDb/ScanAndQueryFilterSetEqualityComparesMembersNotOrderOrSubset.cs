// Translated from behavioral/dynamodb/check_scan_and_query_filter_set_equality_compares_members_not_order_or_subset.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.scan-and-query-filter-set-equality-compares-members-not-order-or-subset</summary>
[Category("AwsEvaluationSuite")]
public sealed class ScanAndQueryFilterSetEqualityComparesMembersNotOrderOrSubset
{
    private const string Check = "scan-and-query-filter-set-equality-compares-members-not-order-or-subset";

    private static byte[] Bin(string text) => Encoding.UTF8.GetBytes(text);

    private static async Task<SortedSet<string>> Scan(IAmazonDynamoDB ddb, string table, string filterExpr, Dictionary<string, AttributeValue> values)
    {
        var resp = await ddb.ScanAsync(new ScanRequest
        {
            TableName = table,
            FilterExpression = filterExpr,
            ExpressionAttributeValues = values,
            ConsistentRead = true,
        });
        return new SortedSet<string>(resp.Items.Select(item => item["sk"].S), StringComparer.Ordinal);
    }

    private static async Task<SortedSet<string>> Query(IAmazonDynamoDB ddb, string table, string pk, string filterExpr, Dictionary<string, AttributeValue> values)
    {
        var allValues = new Dictionary<string, AttributeValue>(values, StringComparer.Ordinal) { [":pk"] = S(pk) };
        var resp = await ddb.QueryAsync(new QueryRequest
        {
            TableName = table,
            KeyConditionExpression = "pk = :pk",
            FilterExpression = filterExpr,
            ExpressionAttributeValues = allValues,
            ConsistentRead = true,
        });
        return new SortedSet<string>(resp.Items.Select(item => item["sk"].S), StringComparer.Ordinal);
    }

    /// <summary>
    /// FilterExpression '=' on an SS/NS/BS attribute matches only the item whose set holds
    /// exactly the value's members, regardless of order; a subset or superset does not match,
    /// and '&lt;>' returns every other item, including one missing the attribute.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.scan-and-query-filter-set-equality-compares-members-not-order-or-subset")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-setfilt-");
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
        Dictionary<string, AttributeValue> SsEq() => Item((":v", SS("a", "b")));
        Dictionary<string, AttributeValue> NsEq() => Item((":v", NS("1", "2")));
        Dictionary<string, AttributeValue> BsEq() => Item((":v", BS(Bin("x"), Bin("y"))));
        foreach (var (attr, eqValues, exactSk) in new[]
        {
            ("ssattr", SsEq(), "exact_ss"),
            ("nsattr", NsEq(), "exact_ns"),
            ("bsattr", BsEq(), "exact_bs"),
        })
        {
            var matched = await Scan(ddb, table, $"{attr} = :v", eqValues);
            Ensure(
                matched.SetEquals([exactSk]),
                $"Scan '{attr} = :v' matched {Py.Repr(matched)}, expected only {{{Py.Repr(exactSk)}}} (member order, a subset and a superset must not match)");
            var complement = await Scan(ddb, table, $"{attr} <> :v", eqValues);
            var expectedComplement = new SortedSet<string>(allSks.Where(s => s != exactSk), StringComparer.Ordinal);
            Ensure(
                complement.SetEquals(expectedComplement),
                $"Scan '{attr} <> :v' matched {Py.Repr(complement)}, expected the exact complement {Py.Repr(expectedComplement)} (including the item missing {Py.Repr(attr)})");
        }

        // Query leg on the same partition: the same '=' and '<>' set semantics hold under a
        // FilterExpression applied after the key condition narrows to one partition.
        var qMatched = await Query(ddb, table, pk, "ssattr = :v", SsEq());
        Ensure(qMatched.SetEquals(["exact_ss"]), $"Query 'ssattr = :v' matched {Py.Repr(qMatched)}, expected only {{'exact_ss'}}");
        var qComplement = await Query(ddb, table, pk, "ssattr <> :v", SsEq());
        var expectedQComplement = new SortedSet<string>(allSks.Where(s => s != "exact_ss"), StringComparer.Ordinal);
        Ensure(
            qComplement.SetEquals(expectedQComplement),
            $"Query 'ssattr <> :v' matched {Py.Repr(qComplement)}, expected the exact complement {Py.Repr(expectedQComplement)}");
    }
}
