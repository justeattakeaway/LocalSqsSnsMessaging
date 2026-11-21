// Translated from behavioral/dynamodb/check_scan_filter_expression_between_mixed_type_bounds_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit abd0811).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.scan-filter-expression-between-mixed-type-bounds-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class ScanFilterExpressionBetweenMixedTypeBoundsRejected
{
    private const string Check = "scan-filter-expression-between-mixed-type-bounds-rejected";
    private const string ErrValidation = "ValidationException";

    /// <summary>The (code, message) <paramref name="fn"/> is refused with. An unexpected success raises, naming it.</summary>
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
    /// A Scan FilterExpression's BETWEEN with bounds of two different types is
    /// ValidationException before any item is examined, while same-typed bounds still select.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.scan-filter-expression-between-mixed-type-bounds-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-ddbbetw-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real", Value = Check }],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(table));
        await ddb.WaitForTableExists(table, delaySeconds: 2, maxAttempts: 250);

        await ddb.PutItemAsync(table, Item(("pk", S("i1")), ("score", N("15"))));

        async Task<List<string>> ScanIds(string filterExpression, Dictionary<string, AttributeValue> values)
        {
            var resp = await ddb.ScanAsync(new ScanRequest
            {
                TableName = table,
                ConsistentRead = true,
                FilterExpression = filterExpression,
                ExpressionAttributeValues = values,
            });
            return resp.Items.Select(item => item["pk"].S).Order(StringComparer.Ordinal).ToList();
        }

        // Positive control: matching-typed bounds parse and select the item inside [10, 25].
        var ok = await ScanIds("score BETWEEN :lo AND :hi", Item((":lo", N("10")), (":hi", N("25"))));
        Ensure(ok.SequenceEqual(["i1"]), $"the same-type-bounds control returned {Py.Repr(ok)}, expected ['i1']");

        var (code, message) = await Error(() => ddb.ScanAsync(new ScanRequest
        {
            TableName = table,
            ConsistentRead = true,
            FilterExpression = "score BETWEEN :lo AND :hi",
            ExpressionAttributeValues = Item((":lo", N("10")), (":hi", S("25"))),
        }));
        Ensure(code == ErrValidation, $"the mixed-type-bounds Scan was refused with {Py.Repr(code)}, expected {Py.Repr(ErrValidation)}");
        const string expectedMessage =
            "Invalid FilterExpression: The BETWEEN operator requires same data type for lower and " +
            "upper bounds; lower bound operand: AttributeValue: {N:10}, upper bound operand: " +
            "AttributeValue: {S:25}";
        Ensure(message == expectedMessage, $"unexpected message for the mixed-type BETWEEN: {Py.Repr(message)}");

        // After the rejection the table still answers the control exactly as before.
        var again = await ScanIds("score BETWEEN :lo AND :hi", Item((":lo", N("10")), (":hi", N("25"))));
        Ensure(again.SequenceEqual(["i1"]), $"after the rejection the control returned {Py.Repr(again)}, expected ['i1']");
    }
}
