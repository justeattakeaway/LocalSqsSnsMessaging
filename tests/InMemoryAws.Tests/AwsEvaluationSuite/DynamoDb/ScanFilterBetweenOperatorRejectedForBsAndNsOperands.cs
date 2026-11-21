// Translated from behavioral/dynamodb/check_scan_filter_between_operator_rejected_for_bs_and_ns_operands.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit abd0811).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.scan-filter-between-operator-rejected-for-bs-and-ns-operands</summary>
[Category("AwsEvaluationSuite")]
public sealed class ScanFilterBetweenOperatorRejectedForBsAndNsOperands
{
    private const string Check = "scan-filter-between-operator-rejected-for-bs-and-ns-operands";

    private static string OperandTypeMessage(string opType) =>
        "Invalid FilterExpression: Incorrect operand type for operator or function; " +
        $"operator or function: BETWEEN, operand type: {opType}";

    /// <summary>A cleanup running <c>undo()</c> where a failed delete raises.</summary>
    private static Func<Task> Teardown(Func<Task> undo, params string[] notFound) => async () =>
    {
        var error = await FidelityContext.CaptureError(undo);
        if (error is not null && !notFound.Contains(error.Code))
        {
            throw error.Exception;
        }
    };

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
    /// A Scan FilterExpression's BETWEEN refuses a Binary Set or Number Set operand with
    /// ValidationException, and leaves the item exactly as it was before the rejected calls.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.scan-filter-between-operator-rejected-for-bs-and-ns-operands")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-ddbbtwn-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            Tags = [new Tag { Key = "sim2real", Value = Check }],
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
        });
        ctx.OnCleanup(Teardown(() => ddb.DeleteTableAsync(table), "ResourceNotFoundException"));
        await ddb.WaitForTableExists(table, delaySeconds: 2, maxAttempts: 250);

        var item = Item(
            ("pk", S("item")),
            ("bs", BS([0x02])),
            ("ns", NS("5")),
            ("score", N("5")));
        await ddb.PutItemAsync(table, item);

        Task<ScanResponse> Scan(string filterExpression, Dictionary<string, AttributeValue> values) =>
            ddb.ScanAsync(new ScanRequest
            {
                TableName = table,
                ConsistentRead = true,
                FilterExpression = filterExpression,
                ExpressionAttributeValues = values,
            });

        foreach (var (attr, opType, lo, hi) in new[]
        {
            ("bs", "BS", BS([0x01]), BS([0x05])),
            ("ns", "NS", NS("1"), NS("9")),
        })
        {
            var (code, message) = await Error(() => Scan($"{attr} BETWEEN :lo AND :hi", Item((":lo", lo), (":hi", hi))));
            Ensure(
                code == "ValidationException",
                $"Scan '{attr} BETWEEN :lo AND :hi' with a {opType} operand answered {Py.Repr(code)}, expected ValidationException");
            var expectedMessage = OperandTypeMessage(opType);
            Ensure(
                message == expectedMessage,
                $"Scan '{attr} BETWEEN :lo AND :hi' with a {opType} operand said {Py.Repr(message)}, expected {Py.Repr(expectedMessage)}");
        }

        // Positive control: BETWEEN over the correctly typed Number attribute still admits the item,
        // proving the rejections above are about the BS/NS operand type, not BETWEEN itself.
        var control = await Scan("score BETWEEN :lo AND :hi", Item((":lo", N("1")), (":hi", N("9"))));
        var controlPks = control.Items.Select(i => i["pk"].S).ToHashSet(StringComparer.Ordinal);
        Ensure(
            controlPks.SetEquals(["item"]),
            $"score BETWEEN 1 AND 9 selected {Py.Repr(controlPks.Order(StringComparer.Ordinal).ToList())}, expected ['item']");

        // The rejected calls read nothing and changed nothing: the item is exactly as it was written.
        var after = (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Item(("pk", S("item"))), ConsistentRead = true })).Item;
        Ensure(
            Py.Equal(after["bs"], item["bs"]) && Py.Equal(after["ns"], item["ns"]),
            $"item changed after the rejected BETWEEN calls: {Py.Repr(after)}");
    }
}
