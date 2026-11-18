// Translated from behavioral/dynamodb/check_ddb_query_filter_size_counts_bytes_elements_or_keys_by_attribute_type.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Globalization;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.ddb-query-filter-size-counts-bytes-elements-or-keys-by-attribute-type</summary>
[Category("AwsEvaluationSuite")]
public sealed class DdbQueryFilterSizeCountsBytesElementsOrKeysByAttributeType
{
    private const string Check = "ddb-query-filter-size-counts-bytes-elements-or-keys-by-attribute-type";

    /// <summary>
    /// sk -> item attrs. Each attribute's two variants are built so that counting its serialized
    /// bytes or its nested characters, rather than B-bytes / L-and-SS-elements / M-keys, would give
    /// the opposite ordering -- a coincidental match cannot pass these assertions.
    /// </summary>
    private static List<(string Sk, (string Name, AttributeValue Value) Attr)> Items() =>
    [
        ("b-small", ("bval", B("ABC"))),
        ("b-big", ("bval", B("ABCDEFG"))),
        ("l-small", ("lval", L(S(new string('x', 50)), S(new string('y', 50))))),
        ("l-big", ("lval", L(N("1"), N("2"), N("3"), N("4"), N("5")))),
        ("ss-small", ("ssval", SS(new string('a', 20), new string('b', 20)))),
        ("ss-big", ("ssval", SS("a", "b", "c", "d", "e"))),
        ("m-small", ("mval", M(("a", S(new string('x', 50))), ("b", S(new string('y', 50)))))),
        ("m-big", ("mval", M(("a", N("1")), ("b", N("2")), ("c", N("3")), ("d", N("4")), ("e", N("5"))))),
    ];

    /// <summary>(attribute name, size of the "-small" item, size of the "-big" item).</summary>
    private static readonly (string Attr, int Small, int Big)[] Cases =
    [
        ("bval", 3, 7),
        ("lval", 2, 5),
        ("ssval", 2, 5),
        ("mval", 2, 5),
    ];

    /// <summary>
    /// A Query FilterExpression's size() counts B's bytes, L's and SS's elements, and M's keys --
    /// not a nested character or serialized-byte total -- selecting exactly the item of that size.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.ddb-query-filter-size-counts-bytes-elements-or-keys-by-attribute-type")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-ddbfsz-");
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

        async Task<HashSet<string>> BySize(string attr, int n)
        {
            var resp = await ddb.QueryAsync(new QueryRequest
            {
                TableName = table,
                KeyConditionExpression = "pk = :p",
                ConsistentRead = true,
                FilterExpression = $"size({attr}) = :n",
                ExpressionAttributeValues = Item((":p", S("probe")), (":n", N(n.ToString(CultureInfo.InvariantCulture)))),
            });
            return [.. resp.Items.Select(i => i["sk"].S)];
        }

        foreach (var (attr, smallN, bigN) in Cases)
        {
            var kind = attr[..^3];
            var (smallSk, bigSk) = ($"{kind}-small", $"{kind}-big");
            var gotSmall = await BySize(attr, smallN);
            Ensure(
                gotSmall.SetEquals([smallSk]),
                $"size({attr}) = {smallN} selected {Py.Repr(gotSmall.Order(StringComparer.Ordinal).ToList())}, expected [{Py.Repr(smallSk)}]");
            var gotBig = await BySize(attr, bigN);
            Ensure(
                gotBig.SetEquals([bigSk]),
                $"size({attr}) = {bigN} selected {Py.Repr(gotBig.Order(StringComparer.Ordinal).ToList())}, expected [{Py.Repr(bigSk)}]");
        }
    }
}
