// Translated from behavioral/dynamodb/check_query_filter_contains_binary_set_selects_exact_member.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit abd0811).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.query-filter-contains-binary-set-selects-exact-member</summary>
[Category("AwsEvaluationSuite")]
public sealed class QueryFilterContainsBinarySetSelectsExactMember
{
    private const string Check = "query-filter-contains-binary-set-selects-exact-member";

    /// <summary>
    /// Query's FilterExpression contains(#bs, :v) on a Binary Set matches only the item whose
    /// set holds :v as one exact member, never an item whose members merely concatenate to it.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.query-filter-contains-binary-set-selects-exact-member")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-ddbqbs-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH), new KeySchemaElement("sk", KeyType.RANGE)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S), new AttributeDefinition("sk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real", Value = Check }],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(table));
        await ddb.WaitForTableExists(table, delaySeconds: 2, maxAttempts: 150);

        const string pk = "p";
        await ddb.PutItemAsync(table, Item(("pk", S(pk)), ("sk", S("joined")), ("codes", BS([0x01, 0x02], [0x10]))));
        await ddb.PutItemAsync(table, Item(("pk", S(pk)), ("sk", S("split")), ("codes", BS([0x01], [0x02], [0x20]))));
        await ddb.PutItemAsync(table, Item(("pk", S(pk)), ("sk", S("joined-too")), ("codes", BS([0x01, 0x02]))));
        await ddb.PutItemAsync(table, Item(("pk", S(pk)), ("sk", S("no-codes"))));

        async Task<HashSet<string>> Query(Dictionary<string, AttributeValue> values, string? filter = null, Dictionary<string, string>? names = null)
        {
            var resp = await ddb.QueryAsync(new QueryRequest
            {
                TableName = table,
                ConsistentRead = true,
                KeyConditionExpression = "pk = :pk",
                FilterExpression = filter,
                ExpressionAttributeNames = names,
                ExpressionAttributeValues = values,
            });
            return resp.Items.Select(i => i["sk"].S).ToHashSet(StringComparer.Ordinal);
        }

        static List<string> Sorted(HashSet<string> set) => [.. set.Order(StringComparer.Ordinal)];

        var baseline = await Query(Item((":pk", S(pk))));
        Ensure(
            baseline.SetEquals(["joined", "split", "joined-too", "no-codes"]),
            $"unfiltered Query returned {Py.Repr(Sorted(baseline))}, expected all four items in the partition");

        var joinedMatch = await Query(Item((":pk", S(pk)), (":v", B([0x01, 0x02]))), "contains(#c, :v)", Names(("#c", "codes")));
        Ensure(
            joinedMatch.SetEquals(["joined", "joined-too"]),
            $"contains(codes, (B)x01x02) returned {Py.Repr(Sorted(joinedMatch))}, expected only the items " +
            "holding b'\\x01\\x02' as one member, never 'split' (which holds b'\\x01' and b'\\x02' " +
            "as two separate members) or 'no-codes' (which lacks the attribute)");

        var splitMatch = await Query(Item((":pk", S(pk)), (":v", B([0x01]))), "contains(#c, :v)", Names(("#c", "codes")));
        Ensure(
            splitMatch.SetEquals(["split"]),
            $"contains(codes, (B)x01) returned {Py.Repr(Sorted(splitMatch))}, expected only 'split' (whose " +
            "set holds the exact one-byte member), never 'joined' or 'joined-too' (whose member is " +
            "the two-byte string, not a match for the one-byte operand)");
    }
}
