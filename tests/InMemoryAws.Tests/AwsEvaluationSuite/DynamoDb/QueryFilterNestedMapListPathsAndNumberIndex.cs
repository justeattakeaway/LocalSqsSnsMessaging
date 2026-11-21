// Translated from behavioral/dynamodb/check_query_filter_nested_map_list_paths_and_number_index.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit abd0811).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.query-filter-nested-map-list-paths-and-number-index</summary>
[Category("AwsEvaluationSuite")]
public sealed class QueryFilterNestedMapListPathsAndNumberIndex
{
    private const string Check = "query-filter-nested-map-list-paths-and-number-index";

    /// <summary>
    /// Five sort keys under one partition, as (sk, has_child, leaf, list_child, num1).
    /// <c>has_child</c> controls whether <c>meta</c> carries a <c>child</c> member; <c>leaf</c> is the
    /// three-level path <c>deep.mid.leaf</c>; <c>list_child</c> is <c>items[0].child</c>; <c>num1</c> is
    /// the Number stored at <c>nums[1]</c>.
    /// </summary>
    private static readonly (string Sk, bool HasChild, string Leaf, string ListChild, string Num1)[] Rows =
    [
        ("a", true, "L1", "x", "5"),
        ("b", false, "L2", "y", "7"),
        ("c", true, "L1", "x", "5"),
        ("d", false, "L1", "y", "5"),
        ("e", true, "L2", "x", "7"),
    ];

    private static Dictionary<string, AttributeValue> ItemOf((string Sk, bool HasChild, string Leaf, string ListChild, string Num1) row)
    {
        var (sk, hasChild, leaf, listChild, num1) = row;
        var meta = hasChild ? M(("child", S("yes"))) : M(("other", S("1")));
        return Item(
            ("pk", S("grp")),
            ("sk", S(sk)),
            ("meta", meta),
            ("deep", M(("mid", M(("leaf", S(leaf)))))),
            ("items", L(M(("child", S(listChild))), S("tail"))),
            ("nums", L(N("0"), N(num1), N("99"))));
    }

    /// <summary>
    /// Query's FilterExpression resolves attribute_exists/attribute_not_exists, equality and a
    /// list-index child on a nested document path, and a three-level map path and a list[i]
    /// holding a Number, each selecting the exact items that hold it.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.query-filter-nested-map-list-paths-and-number-index")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = await Seed(ctx);
        var hasChild = Rows.Where(r => r.HasChild).Select(r => r.Sk).ToHashSet(StringComparer.Ordinal);
        var noChild = Rows.Where(r => !r.HasChild).Select(r => r.Sk).ToHashSet(StringComparer.Ordinal);
        var names = Names(("#m", "meta"), ("#c", "child"));

        var got = await Query(ddb, table, "attribute_exists(#m.#c)", names, []);
        Ensure(got.SetEquals(hasChild), $"attribute_exists(meta.child) returned {Py.Repr(Sorted(got))}, expected {Py.Repr(Sorted(hasChild))}");

        got = await Query(ddb, table, "attribute_not_exists(#m.#c)", names, []);
        Ensure(got.SetEquals(noChild), $"attribute_not_exists(meta.child) returned {Py.Repr(Sorted(got))}, expected {Py.Repr(Sorted(noChild))}");

        var wantLeaf = Rows.Where(r => r.Leaf == "L1").Select(r => r.Sk).ToHashSet(StringComparer.Ordinal);
        got = await Query(ddb, table, "#d.#mi.#l = :l1", Names(("#d", "deep"), ("#mi", "mid"), ("#l", "leaf")), Item((":l1", S("L1"))));
        Ensure(
            got.SetEquals(wantLeaf),
            $"three-level nested path deep.mid.leaf = 'L1' returned {Py.Repr(Sorted(got))}, " +
            $"expected {Py.Repr(Sorted(wantLeaf))}");

        var wantListChild = Rows.Where(r => r.ListChild == "x").Select(r => r.Sk).ToHashSet(StringComparer.Ordinal);
        got = await Query(ddb, table, "#i[0].#c = :x", Names(("#i", "items"), ("#c", "child")), Item((":x", S("x"))));
        Ensure(got.SetEquals(wantListChild), $"items[0].child = 'x' returned {Py.Repr(Sorted(got))}, expected {Py.Repr(Sorted(wantListChild))}");

        var wantNum = Rows.Where(r => r.Num1 == "5").Select(r => r.Sk).ToHashSet(StringComparer.Ordinal);
        got = await Query(ddb, table, "#n[1] = :n5", Names(("#n", "nums")), Item((":n5", N("5"))));
        Ensure(got.SetEquals(wantNum), $"nums[1] = 5 returned {Py.Repr(Sorted(got))}, expected {Py.Repr(Sorted(wantNum))}");
    }

    /// <summary>A fresh pk='grp' table with the five rows, under one BatchWriteItem.</summary>
    private static async Task<string> Seed(FidelityContext ctx)
    {
        var ddb = ctx.Dynamo;
        var name = FidelityContext.UniqueName("sim2real-ddbqfn-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = name,
            Tags = [new Tag { Key = "sim2real", Value = Check }],
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH), new KeySchemaElement("sk", KeyType.RANGE)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S), new AttributeDefinition("sk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(name));
        await ddb.WaitForTableExists(name, delaySeconds: 2, maxAttempts: 250);
        var resp = await ddb.BatchWriteItemAsync(new BatchWriteItemRequest
        {
            RequestItems = new() { [name] = [.. Rows.Select(r => new WriteRequest { PutRequest = new PutRequest { Item = ItemOf(r) } })] },
        });
        Ensure(resp.UnprocessedItems is not { Count: > 0 }, $"seeding BatchWriteItem left {Py.Repr(resp.UnprocessedItems)} unprocessed");
        return name;
    }

    /// <summary>The set of <c>sk</c> values a Query against <paramref name="table"/> returns under one filter form.</summary>
    private static async Task<HashSet<string>> Query(
        IAmazonDynamoDB ddb, string table, string filterExpression, Dictionary<string, string> names, Dictionary<string, AttributeValue> values)
    {
        var resp = await ddb.QueryAsync(new QueryRequest
        {
            TableName = table,
            ConsistentRead = true,
            KeyConditionExpression = "pk = :p",
            FilterExpression = filterExpression,
            ExpressionAttributeNames = names,
            ExpressionAttributeValues = new Dictionary<string, AttributeValue>(values, StringComparer.Ordinal) { [":p"] = S("grp") },
        });
        return [.. resp.Items.Select(i => i["sk"].S)];
    }

    private static List<string> Sorted(HashSet<string> set) => [.. set.Order(StringComparer.Ordinal)];
}
