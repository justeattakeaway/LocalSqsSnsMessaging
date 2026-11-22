// Translated from behavioral/dynamodb/check_query_filter_contains_ns_member_type_strict.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit abd0811).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.query-filter-contains-ns-member-type-strict</summary>
[Category("AwsEvaluationSuite")]
public sealed class QueryFilterContainsNsMemberTypeStrict
{
    private const string Check = "query-filter-contains-ns-member-type-strict";

    /// <summary>
    /// A Query FilterExpression `contains()` with an N operand matches an NS member or an N
    /// element inside an L, never an S, SS or B attribute holding the same digits.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.query-filter-contains-ns-member-type-strict")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var t = FidelityContext.UniqueName("sim2real-behav-ddbqct-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = t,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH), new KeySchemaElement("sk", KeyType.RANGE)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S), new AttributeDefinition("sk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real", Value = Check }],
        });
        ctx.OnCleanup(Teardown(() => ddb.DeleteTableAsync(t), "ResourceNotFoundException"));
        await ddb.WaitForTableExists(t, delaySeconds: 2, maxAttempts: 150);

        const string pk = "p1";
        await ddb.PutItemAsync(t, Item(("pk", S(pk)), ("sk", S("coll-ns")), ("coll", NS("5", "6"))));
        await ddb.PutItemAsync(t, Item(("pk", S(pk)), ("sk", S("coll-l")), ("coll", L(N("5"), S("x")))));
        await ddb.PutItemAsync(t, Item(("pk", S(pk)), ("sk", S("coll-s")), ("coll", S("a5b"))));
        await ddb.PutItemAsync(t, Item(("pk", S(pk)), ("sk", S("coll-ss")), ("coll", SS("5", "6"))));
        await ddb.PutItemAsync(t, Item(("pk", S(pk)), ("sk", S("coll-b")), ("coll", B("5"))));
        const int partitionSize = 5;

        Task<QueryResponse> Query(string filter, AttributeValue v) => ddb.QueryAsync(new QueryRequest
        {
            TableName = t,
            ConsistentRead = true,
            KeyConditionExpression = "#pk = :pk",
            ExpressionAttributeNames = Names(("#pk", "pk")),
            ExpressionAttributeValues = Item((":v", v), (":pk", S(pk))),
            FilterExpression = filter,
        });

        static HashSet<string> Sks(List<Dictionary<string, AttributeValue>> items) => items.Select(i => i["sk"].S).ToHashSet(StringComparer.Ordinal);
        static List<string> Sorted(IEnumerable<string> set) => [.. set.Order(StringComparer.Ordinal)];

        var respN = await Query("contains(coll, :v)", N("5"));
        var containsN = Sks(respN.Items);
        Ensure(
            containsN.SetEquals(["coll-ns", "coll-l"]),
            $"Query contains(coll, (N)5) matched {Py.Repr(Sorted(containsN))}, expected only the NS and L " +
            "items holding an N 5 member/element, never the S, SS or B items holding the same digits");
        Ensure(respN.Count == 2, $"Query contains(coll, (N)5) Count was {Py.Repr(respN.Count)}, expected 2");
        Ensure(
            respN.ScannedCount == partitionSize,
            $"Query contains(coll, (N)5) ScannedCount was {Py.Repr(respN.ScannedCount)}, expected the " +
            $"partition's {partitionSize} items regardless of the filter");

        // Positive control: contains() over the correctly typed (S) operand still matches the S and
        // SS items, proving the filtering mechanism discriminates by value, not merely by excluding N.
        var respS = await Query("contains(coll, :v)", S("5"));
        var containsS = Sks(respS.Items);
        Ensure(
            containsS.SetEquals(["coll-s", "coll-ss"]),
            $"Query contains(coll, (S)'5') matched {Py.Repr(Sorted(containsS))}, expected the S item " +
            "(substring) and the SS item (member), never the NS, L or B items");

        var respB = await Query("contains(coll, :v)", B("5"));
        var containsB = Sks(respB.Items);
        Ensure(
            containsB.SetEquals(["coll-b"]),
            $"Query contains(coll, (B)'5') matched {Py.Repr(Sorted(containsB))}, expected only the B item");
    }

    /// <summary>A cleanup running <paramref name="undo"/> where a failed delete raises.</summary>
    private static Func<Task> Teardown(Func<Task> undo, params string[] notFound) => async () =>
    {
        var error = await FidelityContext.CaptureError(undo);
        if (error is not null && !notFound.Contains(error.Code))
        {
            throw error.Exception;
        }
    };
}
