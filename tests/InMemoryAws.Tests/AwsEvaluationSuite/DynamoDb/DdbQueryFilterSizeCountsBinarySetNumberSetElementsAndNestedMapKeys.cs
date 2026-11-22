// Translated from behavioral/dynamodb/check_ddb_query_filter_size_counts_binary_set_number_set_elements_and_nested_map_keys.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit abd0811).
using System.Globalization;
using System.Text;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.ddb-query-filter-size-counts-binary-set-number-set-elements-and-nested-map-keys</summary>
[Category("AwsEvaluationSuite")]
public sealed class DdbQueryFilterSizeCountsBinarySetNumberSetElementsAndNestedMapKeys
{
    private const string Check = "ddb-query-filter-size-counts-binary-set-number-set-elements-and-nested-map-keys";

    /// <summary>
    /// sk -> item attrs. Each pair's "-small"/"-big" variant is built so that counting a
    /// different metric (total bytes, total digits, or the outer map's own key count instead of
    /// the nested map's) gives the opposite ordering, so a coincidental match cannot pass.
    /// </summary>
    private static List<(string Sk, (string Name, AttributeValue Value) Attr)> Items() =>
    [
        ("bs-small", ("bsval", BS(Repeat('x', 10), Repeat('y', 10)))),
        ("bs-big", ("bsval", BS(Repeat('a', 2), Repeat('b', 2), Repeat('c', 2), Repeat('d', 2)))),
        ("ns-small", ("nsval", NS("99999999", "88888888"))),
        ("ns-big", ("nsval", NS("1", "2", "3", "4"))),
        ("map-small", ("mval", M(
            ("child", M(("a", N("1")), ("b", N("2")))),
            ("k1", S("x")),
            ("k2", S("y"))))),
        ("map-big", ("mval", M(
            ("child", M(("a", N("1")), ("b", N("2")), ("c", N("3")), ("d", N("4"))))))),
    ];

    /// <summary>(sk prefix, expression path, name map, size of "-small" item, size of "-big" item).</summary>
    private static readonly (string Prefix, string Path, Dictionary<string, string>? Names, int Small, int Big)[] Cases =
    [
        ("bs", "bsval", null, 2, 4),
        ("ns", "nsval", null, 2, 4),
        ("map", "#o.#c", Names(("#o", "mval"), ("#c", "child")), 2, 4),
    ];

    private static byte[] Repeat(char c, int n) => Encoding.ASCII.GetBytes(new string(c, n));

    /// <summary>
    /// A Query FilterExpression's size() counts a Binary Set's and a Number Set's elements and a
    /// nested map's own keys -- not a byte total, a digit total or the enclosing map's key count.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.ddb-query-filter-size-counts-binary-set-number-set-elements-and-nested-map-keys")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-ddbfszx-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            Tags = [new Tag { Key = "sim2real", Value = Check }],
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH), new KeySchemaElement("sk", KeyType.RANGE)],
            AttributeDefinitions =
            [
                new AttributeDefinition("pk", ScalarAttributeType.S),
                new AttributeDefinition("sk", ScalarAttributeType.S),
            ],
            BillingMode = BillingMode.PAY_PER_REQUEST,
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(table));
        await ddb.WaitForTableExists(table, delaySeconds: 2, maxAttempts: 250);

        foreach (var (sk, attr) in Items())
        {
            await ddb.PutItemAsync(table, Item(("pk", S("probe")), ("sk", S(sk)), attr));
        }

        async Task<HashSet<string>> BySize(string path, Dictionary<string, string>? names, int n)
        {
            var request = new QueryRequest
            {
                TableName = table,
                KeyConditionExpression = "pk = :p",
                ConsistentRead = true,
                FilterExpression = $"size({path}) = :n",
                ExpressionAttributeValues = Item((":p", S("probe")), (":n", N(n.ToString(CultureInfo.InvariantCulture)))),
            };
            if (names is not null)
            {
                request.ExpressionAttributeNames = names;
            }

            var resp = await ddb.QueryAsync(request);
            return [.. resp.Items.Select(i => i["sk"].S)];
        }

        static List<string> Sorted(HashSet<string> set) => [.. set.Order(StringComparer.Ordinal)];

        foreach (var (prefix, path, names, smallN, bigN) in Cases)
        {
            var (smallSk, bigSk) = ($"{prefix}-small", $"{prefix}-big");
            var gotSmall = await BySize(path, names, smallN);
            Ensure(
                gotSmall.SetEquals([smallSk]),
                $"size({path}) = {smallN} selected {Py.Repr(Sorted(gotSmall))}, expected [{Py.Repr(smallSk)}]");
            var gotBig = await BySize(path, names, bigN);
            Ensure(
                gotBig.SetEquals([bigSk]),
                $"size({path}) = {bigN} selected {Py.Repr(Sorted(gotBig))}, expected [{Py.Repr(bigSk)}]");
        }

        // Positive control: size() on the outer map alone (3 keys vs. 1) proves the table and the
        // comparator work and that size(mval.child) above is reading the nested map, not mval itself.
        var outerControl = await BySize("mval", null, 3);
        Ensure(
            outerControl.SetEquals(["map-small"]),
            $"size(mval) = 3 selected {Py.Repr(Sorted(outerControl))}, expected [{Py.Repr("map-small")}]");
    }
}
