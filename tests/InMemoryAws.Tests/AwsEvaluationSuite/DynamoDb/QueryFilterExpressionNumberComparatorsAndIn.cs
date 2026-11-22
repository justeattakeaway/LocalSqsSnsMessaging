// Translated from behavioral/dynamodb/check_query_filter_expression_number_comparators_and_in.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit abd0811).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.query-filter-expression-number-comparators-and-in</summary>
[Category("AwsEvaluationSuite")]
public sealed class QueryFilterExpressionNumberComparatorsAndIn
{
    private const string Check = "query-filter-expression-number-comparators-and-in";

    /// <summary>
    /// Six items in one partition. Scores are chosen so a byte-wise string compare disagrees with
    /// a numeric one ("9" > "10" and "100" < "11" as strings), so each case below only passes if
    /// DynamoDB compares N values numerically.
    /// </summary>
    private static readonly (string Sk, string Score)[] Rows =
    [
        ("i1", "5"),
        ("i2", "9"),
        ("i3", "10"),
        ("i4", "11"),
        ("i5", "100"),
        ("i6", "200"),
    ];

    /// <summary>
    /// Query FilterExpression >=, <=, < and IN on a Number attribute compare the stored values
    /// numerically, selecting the items a numeric (not byte-wise) comparison predicts.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.query-filter-expression-number-comparators-and-in")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-");
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

        foreach (var (sk, score) in Rows)
        {
            await ddb.PutItemAsync(table, Item(("pk", S("p1")), ("sk", S(sk)), ("score", N(score))));
        }

        var readback = await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Item(("pk", S("p1")), ("sk", S("i5"))), ConsistentRead = true });
        Ensure(
            Py.Equal(readback.Item["score"], N("100")),
            $"GetItem returned score {Py.Repr(readback.Item["score"])}, expected the exact stored N string {{'N': '100'}}");

        var partitionSize = Rows.Length;
        (string Expr, Dictionary<string, AttributeValue> Values, string[] Expected)[] cases =
        [
            ("#sc >= :v", Item((":v", N("10"))), ["i3", "i4", "i5", "i6"]),
            ("#sc <= :v", Item((":v", N("10"))), ["i1", "i2", "i3"]),
            ("#sc < :v", Item((":v", N("11"))), ["i1", "i2", "i3"]),
            ("#sc IN (:a, :b, :c)", Item((":a", N("9")), (":b", N("11")), (":c", N("200"))), ["i2", "i4", "i6"]),
        ];
        foreach (var (expr, values, expected) in cases)
        {
            var expressionValues = new Dictionary<string, AttributeValue>(values, StringComparer.Ordinal) { [":pk"] = S("p1") };
            var resp = await ddb.QueryAsync(new QueryRequest
            {
                TableName = table,
                ConsistentRead = true,
                KeyConditionExpression = "#pk = :pk",
                FilterExpression = expr,
                ExpressionAttributeNames = Names(("#pk", "pk"), ("#sc", "score")),
                ExpressionAttributeValues = expressionValues,
            });
            var got = resp.Items.Select(i => i["sk"].S).ToHashSet(StringComparer.Ordinal);
            Ensure(
                got.SetEquals(expected),
                $"Query {Py.Repr(expr)} returned {Py.Repr(got.Order(StringComparer.Ordinal).ToList())}, expected {Py.Repr(expected.Order(StringComparer.Ordinal).ToList())}");
            Ensure(resp.Count == expected.Length, $"Query {Py.Repr(expr)} Count was {resp.Count}, expected {expected.Length}");
            Ensure(
                resp.ScannedCount == partitionSize,
                $"Query {Py.Repr(expr)} ScannedCount was {resp.ScannedCount}, expected the partition's " +
                $"{partitionSize} items regardless of the filter");
        }
    }
}
