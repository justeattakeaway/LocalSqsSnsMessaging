// Translated from behavioral/dynamodb/check_scan_filter_expression_lt_gt_non_orderable_operand_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit abd0811).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.scan-filter-expression-lt-gt-non-orderable-operand-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class ScanFilterExpressionLtGtNonOrderableOperandRejected
{
    private const string Check = "scan-filter-expression-lt-gt-non-orderable-operand-rejected";

    private static string OperandTypeMsg(string op, string opType) =>
        "Invalid FilterExpression: Incorrect operand type for operator or function; " +
        $"operator or function: {op}, operand type: {opType}";

    private static readonly (string Type, Func<AttributeValue> Operand)[] NonOrderable =
    [
        ("BOOL", () => Bool(true)),
        ("NULL", Null),
    ];

    /// <summary>(code, message) of the error <paramref name="fn"/> raises; raises if it unexpectedly succeeds.</summary>
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
    /// A Scan FilterExpression's '&lt;' and '&gt;' refuse a BOOL or NULL operand with
    /// ValidationException naming the operator and the type, both items stay in the table, and
    /// '&lt;' over an N operand still filters.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.scan-filter-expression-lt-gt-non-orderable-operand-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-scanltgt-");
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

        await ddb.PutItemAsync(table, Item(("id", S("item1")), ("amount", N("-1.5"))));
        await ddb.PutItemAsync(table, Item(("id", S("item2")), ("amount", N("3.75"))));

        Task<ScanResponse> ScanCompare(string op, AttributeValue operandValue) =>
            ddb.ScanAsync(new ScanRequest
            {
                TableName = table,
                FilterExpression = $"amount {op} :v",
                ExpressionAttributeValues = Item((":v", operandValue)),
                ConsistentRead = true,
            });

        foreach (var op in new[] { "<", ">" })
        {
            foreach (var (opType, operand) in NonOrderable)
            {
                var (code, message) = await Error(() => ScanCompare(op, operand()));
                Ensure(
                    code == "ValidationException",
                    $"'amount {op} :v' with a {opType} operand answered {Py.Repr(code)}, expected " +
                    "ValidationException");
                var expectedMessage = OperandTypeMsg(op, opType);
                Ensure(
                    message == expectedMessage,
                    $"'amount {op} :v' with a {opType} operand said {Py.Repr(message)}, expected " +
                    $"{Py.Repr(expectedMessage)}");
            }
        }

        var unfiltered = await ddb.ScanAsync(new ScanRequest { TableName = table, ConsistentRead = true });
        Ensure(
            unfiltered.Count == 2,
            $"after the rejected comparisons the table held {unfiltered.Count} item(s), expected 2: " +
            "the rejected reads must not have written or removed anything");

        // Positive control: the same operators over a correctly-typed N operand still filter normally,
        // including a negative decimal bound.
        var lt = await ddb.ScanAsync(new ScanRequest
        {
            TableName = table,
            FilterExpression = "amount < :v",
            ExpressionAttributeValues = Item((":v", N("0"))),
            ConsistentRead = true,
        });
        var ltIds = new SortedSet<string>(lt.Items.Select(item => item["id"].S), StringComparer.Ordinal);
        Ensure(ltIds.SetEquals(["item1"]), $"'amount < 0' with a correctly-typed N operand matched {Py.Repr(ltIds)}");
    }
}
