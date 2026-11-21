// Translated from behavioral/dynamodb/check_lsi_query_sort_key_comparisons_return_full_item_body.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit abd0811).
using System.Globalization;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.lsi-query-sort-key-comparisons-return-full-item-body</summary>
[Category("AwsEvaluationSuite")]
public sealed class LsiQuerySortKeyComparisonsReturnFullItemBody
{
    private const string Check = "lsi-query-sort-key-comparisons-return-full-item-body";

    /// <summary>(sk, lsiSk, payload) rows under one partition; lsiSk values sort lexically as written.</summary>
    private static readonly (string Sk, string LsiSk, string Payload)[] Rows =
    [
        ("rev-0", "d010", "alpha"),
        ("rev-1", "d020", "bravo"),
        ("rev-2", "d030", "charlie"),
        ("rev-3", "d040", "delta"),
        ("rev-4", "d050", "echo"),
        ("rev-5", "e010", "foxtrot"),
        ("rev-6", "e020", "golf"),
    ];

    private static Dictionary<string, AttributeValue> ItemOf(string sk, string lsiSk, string payload, int seq) => Item(
        ("pk", S("item-1")),
        ("sk", S(sk)),
        ("lsiSk", S(lsiSk)),
        ("payload", S(payload)),
        ("seq", N(seq.ToString(CultureInfo.InvariantCulture))));

    private static List<Dictionary<string, AttributeValue>> Expected(params int[] indices) =>
        [.. indices.Select(i => ItemOf(Rows[i].Sk, Rows[i].LsiSk, Rows[i].Payload, seq: i))];

    /// <summary>
    /// A Query against an LSI with =, &lt;, &lt;=, &gt;=, &gt;, BETWEEN or begins_with on the sort key
    /// returns the complete matching item bodies, not merely their keys, in ascending index order.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.lsi-query-sort-key-comparisons-return-full-item-body")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-lsifull-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH), new KeySchemaElement("sk", KeyType.RANGE)],
            AttributeDefinitions =
            [
                new AttributeDefinition("pk", ScalarAttributeType.S),
                new AttributeDefinition("sk", ScalarAttributeType.S),
                new AttributeDefinition("lsiSk", ScalarAttributeType.S),
            ],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            LocalSecondaryIndexes =
            [
                new LocalSecondaryIndex
                {
                    IndexName = "lsi-ops",
                    KeySchema = [new KeySchemaElement("pk", KeyType.HASH), new KeySchemaElement("lsiSk", KeyType.RANGE)],
                    Projection = new Projection { ProjectionType = ProjectionType.ALL },
                },
            ],
            Tags = [new Tag { Key = "sim2real", Value = Check }],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(table));
        await ddb.WaitForTableExists(table, delaySeconds: 2, maxAttempts: 250);

        for (var seq = 0; seq < Rows.Length; seq++)
        {
            var (sk, lsiSk, payload) = Rows[seq];
            await ddb.PutItemAsync(table, ItemOf(sk, lsiSk, payload, seq));
        }

        Task<QueryResponse> Query(string expr, params (string Name, AttributeValue Value)[] values) => ddb.QueryAsync(new QueryRequest
        {
            TableName = table,
            IndexName = "lsi-ops",
            KeyConditionExpression = expr,
            ExpressionAttributeValues = Item([(":pk", S("item-1")), .. values]),
            ConsistentRead = true,
        });

        var eq = await Query("pk = :pk AND lsiSk = :v", (":v", S("d030")));
        AssertFullBody("lsiSk = 'd030'", eq, Expected(2));
        var lt = await Query("pk = :pk AND lsiSk < :v", (":v", S("d030")));
        AssertFullBody("lsiSk < 'd030'", lt, Expected(0, 1));
        var le = await Query("pk = :pk AND lsiSk <= :v", (":v", S("d030")));
        AssertFullBody("lsiSk <= 'd030'", le, Expected(0, 1, 2));
        var ge = await Query("pk = :pk AND lsiSk >= :v", (":v", S("d030")));
        AssertFullBody("lsiSk >= 'd030'", ge, Expected(2, 3, 4, 5, 6));
        var gt = await Query("pk = :pk AND lsiSk > :v", (":v", S("d030")));
        AssertFullBody("lsiSk > 'd030'", gt, Expected(3, 4, 5, 6));
        var between = await Query("pk = :pk AND lsiSk BETWEEN :lo AND :hi", (":lo", S("d020")), (":hi", S("d040")));
        AssertFullBody("lsiSk BETWEEN 'd020' AND 'd040'", between, Expected(1, 2, 3));
        var begins = await Query("pk = :pk AND begins_with(lsiSk, :p)", (":p", S("e")));
        AssertFullBody("begins_with(lsiSk, 'e')", begins, Expected(5, 6));
    }

    private static void AssertFullBody(string label, QueryResponse resp, List<Dictionary<string, AttributeValue>> expected)
    {
        Ensure(Py.Equal(resp.Items, expected), $"{label}: items were {Py.Repr(resp.Items)}, expected {Py.Repr(expected)}");
        Ensure(resp.Count == expected.Count, $"{label}: Count was {resp.Count}, expected {expected.Count}");
        Ensure(resp.ScannedCount == expected.Count, $"{label}: ScannedCount was {resp.ScannedCount}, expected {expected.Count}");
    }
}
