// Translated from behavioral/dynamodb/check_query_filter_contains_on_list_by_element_type.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit abd0811).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.query-filter-contains-on-list-by-element-type</summary>
[Category("AwsEvaluationSuite")]
public sealed class QueryFilterContainsOnListByElementType
{
    private const string Check = "query-filter-contains-on-list-by-element-type";

    /// <summary>
    /// Items in the queried partition: (sk, list elements). Expected sets below are derived from
    /// the typed members themselves, never hardcoded.
    /// </summary>
    private static List<(string Sk, AttributeValue[] Coll)> Rows() =>
    [
        ("i1", [N("5"), S("x")]),
        ("i2", [S("5"), S("y")]),
        ("i3", [N("7")]),
        ("i4", [S("z")]),
    ];

    /// <summary>
    /// Query's FilterExpression contains(list_attr, :v) matches only items whose List holds a
    /// member of :v's exact type, whether :v is N or S, over the whole partition it scans.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.query-filter-contains-on-list-by-element-type")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var t = FidelityContext.UniqueName("sim2real-ddbqfcl-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = t,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH), new KeySchemaElement("sk", KeyType.RANGE)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S), new AttributeDefinition("sk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real", Value = Check }],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(t));
        await ddb.WaitForTableExists(t, delaySeconds: 2, maxAttempts: 250);

        var rows = Rows();
        foreach (var (sk, coll) in rows)
        {
            await ddb.PutItemAsync(t, Item(("pk", S("p1")), ("sk", S(sk)), ("coll", L(coll))));
        }

        Task<QueryResponse> QueryContains(AttributeValue value) => ddb.QueryAsync(new QueryRequest
        {
            TableName = t,
            KeyConditionExpression = "#pk = :pk",
            FilterExpression = "contains(#c, :v)",
            ExpressionAttributeNames = Names(("#pk", "pk"), ("#c", "coll")),
            ExpressionAttributeValues = Item((":pk", S("p1")), (":v", value)),
            ConsistentRead = true,
        });

        static HashSet<string> Ids(List<Dictionary<string, AttributeValue>> items) => items.Select(i => i["sk"].S).ToHashSet(StringComparer.Ordinal);
        static List<string> Sorted(IEnumerable<string> set) => [.. set.Order(StringComparer.Ordinal)];

        var expectedN = rows.Where(r => r.Coll.Any(e => Py.Equal(e, N("5")))).Select(r => r.Sk).ToHashSet(StringComparer.Ordinal);
        var respN = await QueryContains(N("5"));
        var gotN = Ids(respN.Items);
        Ensure(gotN.SetEquals(expectedN), $"contains(coll, (N)'5') returned {Py.Repr(Sorted(gotN))}, expected {Py.Repr(Sorted(expectedN))}");
        Ensure(respN.Count == expectedN.Count, $"contains(coll, (N)'5') Count was {Py.Repr(respN.Count)}, expected {Py.Repr(expectedN.Count)}");
        Ensure(
            respN.ScannedCount == rows.Count,
            $"contains(coll, (N)'5') ScannedCount was {Py.Repr(respN.ScannedCount)}, expected the " +
            $"partition's {rows.Count} items regardless of the filter");

        var expectedS = rows.Where(r => r.Coll.Any(e => Py.Equal(e, S("5")))).Select(r => r.Sk).ToHashSet(StringComparer.Ordinal);
        var respS = await QueryContains(S("5"));
        var gotS = Ids(respS.Items);
        Ensure(gotS.SetEquals(expectedS), $"contains(coll, (S)'5') returned {Py.Repr(Sorted(gotS))}, expected {Py.Repr(Sorted(expectedS))}");
        Ensure(respS.Count == expectedS.Count, $"contains(coll, (S)'5') Count was {Py.Repr(respS.Count)}, expected {Py.Repr(expectedS.Count)}");
        Ensure(
            respS.ScannedCount == rows.Count,
            $"contains(coll, (S)'5') ScannedCount was {Py.Repr(respS.ScannedCount)}, expected the " +
            $"partition's {rows.Count} items regardless of the filter");

        Ensure(
            !gotN.SetEquals(gotS),
            "contains() with an N operand and an S operand of the same digits must select " +
            $"different items; both returned {Py.Repr(Sorted(gotN))}");
    }
}
