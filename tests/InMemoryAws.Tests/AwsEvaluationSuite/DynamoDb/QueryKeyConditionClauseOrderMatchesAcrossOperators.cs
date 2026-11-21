// Translated from behavioral/dynamodb/check_query_key_condition_clause_order_matches_across_operators.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit abd0811).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.query-key-condition-clause-order-matches-across-operators</summary>
[Category("AwsEvaluationSuite")]
public sealed class QueryKeyConditionClauseOrderMatchesAcrossOperators
{
    private const string Check = "query-key-condition-clause-order-matches-across-operators";

    /// <summary>
    /// A Query's KeyConditionExpression returns the identical ordered Items list whether the
    /// sort-key clause is written before or after the partition-key clause, for a '>=' comparator,
    /// BETWEEN and begins_with alike; ScanIndexForward=False reverses both orderings the same way.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.query-key-condition-clause-order-matches-across-operators")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-ddb-kcorder-");
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

        var described = (await ddb.DescribeTableAsync(table)).Table;
        Ensure(described.TableStatus == TableStatus.ACTIVE, $"table {Py.Repr(table)} is {Py.Repr(described.TableStatus)}, expected ACTIVE");
        var keySchema = described.KeySchema.Select(k => new Dictionary<string, object?> { ["AttributeName"] = k.AttributeName, ["KeyType"] = k.KeyType.Value }).ToList();
        Ensure(
            keySchema.Count == 2
                && keySchema[0]["AttributeName"] as string == "pk" && keySchema[0]["KeyType"] as string == "HASH"
                && keySchema[1]["AttributeName"] as string == "sk" && keySchema[1]["KeyType"] as string == "RANGE",
            $"DescribeTable reports key schema {Py.Repr(keySchema)}");

        foreach (var sk in new[] { "a", "b0", "b1" })
        {
            await ddb.PutItemAsync(table, Item(("pk", S("p1")), ("sk", S(sk))));
        }

        await ddb.PutItemAsync(table, Item(("pk", S("p2")), ("sk", S("b0"))));

        List<Dictionary<string, AttributeValue>> wantAsc =
        [
            Item(("pk", S("p1")), ("sk", S("b0"))),
            Item(("pk", S("p1")), ("sk", S("b1"))),
        ];
        var wantDesc = Enumerable.Reverse(wantAsc).ToList();

        async Task<List<Dictionary<string, AttributeValue>>> Query(string cond, Dictionary<string, AttributeValue> values, bool forward) =>
            (await ddb.QueryAsync(new QueryRequest
            {
                TableName = table,
                KeyConditionExpression = cond,
                ExpressionAttributeValues = values,
                ScanIndexForward = forward,
                ConsistentRead = true,
            })).Items;

        (string SkFirst, string PkFirst, Dictionary<string, AttributeValue> Values)[] forms =
        [
            ("sk >= :s AND pk = :p", "pk = :p AND sk >= :s", Item((":p", S("p1")), (":s", S("b0")))),
            (
                "sk BETWEEN :lo AND :hi AND pk = :p", "pk = :p AND sk BETWEEN :lo AND :hi",
                Item((":p", S("p1")), (":lo", S("b0")), (":hi", S("b1")))
            ),
            (
                "begins_with(sk, :q) AND pk = :p", "pk = :p AND begins_with(sk, :q)",
                Item((":p", S("p1")), (":q", S("b")))
            ),
        ];
        foreach (var (skFirst, pkFirst, values) in forms)
        {
            var gotSkFirst = await Query(skFirst, values, forward: true);
            var gotPkFirst = await Query(pkFirst, values, forward: true);
            Ensure(Py.Equal(gotSkFirst, wantAsc), $"{Py.Repr(skFirst)} returned {Py.Repr(gotSkFirst)}, expected {Py.Repr(wantAsc)}");
            Ensure(Py.Equal(gotPkFirst, wantAsc), $"{Py.Repr(pkFirst)} returned {Py.Repr(gotPkFirst)}, expected {Py.Repr(wantAsc)}");
            var gotSkFirstDesc = await Query(skFirst, values, forward: false);
            var gotPkFirstDesc = await Query(pkFirst, values, forward: false);
            Ensure(
                Py.Equal(gotSkFirstDesc, wantDesc),
                $"{Py.Repr(skFirst)} with ScanIndexForward=False returned {Py.Repr(gotSkFirstDesc)}, expected {Py.Repr(wantDesc)}");
            Ensure(
                Py.Equal(gotPkFirstDesc, wantDesc),
                $"{Py.Repr(pkFirst)} with ScanIndexForward=False returned {Py.Repr(gotPkFirstDesc)}, expected {Py.Repr(wantDesc)}");
        }
    }
}
