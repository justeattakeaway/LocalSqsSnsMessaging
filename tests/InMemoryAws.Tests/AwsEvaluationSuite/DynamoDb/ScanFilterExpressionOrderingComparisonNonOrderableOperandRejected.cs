// Translated from behavioral/dynamodb/check_scan_filter_expression_ordering_comparison_non_orderable_operand_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.scan-filter-expression-ordering-comparison-non-orderable-operand-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class ScanFilterExpressionOrderingComparisonNonOrderableOperandRejected
{
    private const string Check = "scan-filter-expression-ordering-comparison-non-orderable-operand-rejected";

    private static string OperandTypeMsg(string op, string opType) =>
        "Invalid FilterExpression: Incorrect operand type for operator or function; " +
        $"operator or function: {op}, operand type: {opType}";

    private static (string OpType, AttributeValue Operand)[] NonOrderable() => [("BOOL", Bool(true)), ("NULL", Null())];

    /// <summary>(code, message) of the ClientError <paramref name="fn"/> raises; RAISES if it unexpectedly succeeds.</summary>
    private static async Task<(string? Code, string? Message)> Error<T>(Func<Task<T>> fn)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is null)
        {
            throw Fail($"expected the call to be refused, but it succeeded: {result}");
        }

        return (error.Code, error.Message);
    }

    /// <summary>
    /// A Scan FilterExpression's &lt;= and >= refuse a BOOL or NULL operand with ValidationException,
    /// and leave the table's content exactly as scanned before the rejected calls.
    ///
    /// Reference: https://docs.aws.amazon.com/amazondynamodb/latest/developerguide/Expressions.OperatorsAndFunctions.html
    /// </summary>
    [Test]
    [DisplayName("dynamodb.scan-filter-expression-ordering-comparison-non-orderable-operand-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-scanord-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            KeySchema = [new KeySchemaElement("id", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("id", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real", Value = Check }],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(table));
        await ddb.WaitForTableExists(table, delaySeconds: 2, maxAttempts: 250);
        await ddb.PutItemAsync(table, Item(("id", S("item1")), ("score", N("10"))));
        await ddb.PutItemAsync(table, Item(("id", S("item2")), ("score", N("20"))));

        Task<ScanResponse> ScanCompare(string op, AttributeValue operandValue) => ddb.ScanAsync(new ScanRequest
        {
            TableName = table,
            FilterExpression = $"score {op} :v",
            ExpressionAttributeValues = Item((":v", operandValue)),
            ConsistentRead = true,
        });

        foreach (var op in new[] { "<=", ">=" })
        {
            foreach (var (opType, operand) in NonOrderable())
            {
                var (code, message) = await Error(() => ScanCompare(op, operand));
                Ensure(code == "ValidationException", $"'score {op} :v' with a {opType} operand answered {Py.Repr(code)}, expected ValidationException");
                var expectedMessage = OperandTypeMsg(op, opType);
                Ensure(message == expectedMessage, $"'score {op} :v' with a {opType} operand said {Py.Repr(message)}, expected {Py.Repr(expectedMessage)}");
            }
        }

        var unfiltered = await ddb.ScanAsync(new ScanRequest { TableName = table, ConsistentRead = true });
        Ensure(
            unfiltered.Count == 2,
            $"after the rejected comparisons the table held {unfiltered.Count} item(s), expected 2: the rejected reads must not have written or removed anything");

        // Positive control: the same operators over a correctly-typed N operand still filter normally.
        var le = await ddb.ScanAsync(new ScanRequest
        {
            TableName = table,
            FilterExpression = "score <= :v",
            ExpressionAttributeValues = Item((":v", N("10"))),
            ConsistentRead = true,
        });
        var leIds = le.Items.Select(item => item["id"].S).ToHashSet(StringComparer.Ordinal);
        Ensure(
            leIds.SetEquals(["item1"]),
            $"'score <= 10' with a correctly-typed N operand matched {{{string.Join(", ", leIds.Order(StringComparer.Ordinal).Select(i => Py.Repr(i)))}}}");
    }
}
