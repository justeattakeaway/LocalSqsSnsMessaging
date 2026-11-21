// Translated from behavioral/dynamodb/check_query_filter_expression_ordering_comparison_non_orderable_operand_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit abd0811).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.query-filter-expression-ordering-comparison-non-orderable-operand-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class QueryFilterExpressionOrderingComparisonNonOrderableOperandRejected
{
    private const string Check = "query-filter-expression-ordering-comparison-non-orderable-operand-rejected";

    private static string OperandTypeMessage(string op, string opType) =>
        "Invalid FilterExpression: Incorrect operand type for operator or function; " +
        $"operator or function: {op}, operand type: {opType}";

    private static List<(string Type, AttributeValue Operand)> NonOrderable() =>
    [
        ("BOOL", Bool(true)),
        ("NULL", Null()),
    ];

    /// <summary>
    /// A Query FilterExpression's &lt;= and &gt;= refuse a BOOL or NULL operand with ValidationException,
    /// and leave the partition's content exactly as queried before the rejected calls.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.query-filter-expression-ordering-comparison-non-orderable-operand-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-queryord-");
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
        await ddb.PutItemAsync(table, Item(("pk", S("p1")), ("sk", S("item1")), ("score", N("10"))));
        await ddb.PutItemAsync(table, Item(("pk", S("p1")), ("sk", S("item2")), ("score", N("20"))));

        Task<QueryResponse> QueryCompare(string op, AttributeValue operandValue) => ddb.QueryAsync(new QueryRequest
        {
            TableName = table,
            KeyConditionExpression = "pk = :pk",
            FilterExpression = $"score {op} :v",
            ExpressionAttributeValues = Item((":pk", S("p1")), (":v", operandValue)),
            ConsistentRead = true,
        });

        foreach (var op in new[] { "<=", ">=" })
        {
            foreach (var (opType, operand) in NonOrderable())
            {
                var (code, message) = await Error(() => QueryCompare(op, operand));
                Ensure(
                    code == "ValidationException",
                    $"'score {op} :v' with a {opType} operand answered {Py.Repr(code)}, expected " +
                    "ValidationException");
                var expectedMessage = OperandTypeMessage(op, opType);
                Ensure(
                    message == expectedMessage,
                    $"'score {op} :v' with a {opType} operand said {Py.Repr(message)}, expected " +
                    $"{Py.Repr(expectedMessage)}");
            }
        }

        var unfiltered = await ddb.QueryAsync(new QueryRequest
        {
            TableName = table,
            KeyConditionExpression = "pk = :pk",
            ExpressionAttributeValues = Item((":pk", S("p1"))),
            ConsistentRead = true,
        });
        Ensure(
            unfiltered.Count == 2,
            $"after the rejected comparisons the partition held {unfiltered.Count} item(s), expected " +
            "2: the rejected filter calls must not have written or removed anything");

        // Positive control: the same operator over a correctly-typed N operand still filters normally.
        var le = await ddb.QueryAsync(new QueryRequest
        {
            TableName = table,
            KeyConditionExpression = "pk = :pk",
            FilterExpression = "score <= :v",
            ExpressionAttributeValues = Item((":pk", S("p1")), (":v", N("10"))),
            ConsistentRead = true,
        });
        var leIds = le.Items.Select(item => item["sk"].S).ToHashSet(StringComparer.Ordinal);
        Ensure(
            leIds.SetEquals(["item1"]),
            $"'score <= 10' with a correctly-typed N operand matched {{{string.Join(", ", leIds.Select(Py.Repr))}}}");
    }

    /// <summary>(code, message) of the error <paramref name="fn"/> raises; RAISES if it unexpectedly succeeds.</summary>
    private static async Task<(string? Code, string? Message)> Error<T>(Func<Task<T>> fn)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is not null)
        {
            return (error.Code, error.Message);
        }

        throw Fail($"expected the call to be refused, but it succeeded: {Py.Repr(result)}");
    }
}
