// Translated from behavioral/dynamodb/check_scan_filter_expression_non_distinct_function_nested_path_operands_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit abd0811).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.scan-filter-expression-non-distinct-function-nested-path-operands-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class ScanFilterExpressionNonDistinctFunctionNestedPathOperandsRejected
{
    private const string Check = "scan-filter-expression-non-distinct-function-nested-path-operands-rejected";

    private static readonly (string Code, string Message) NotDistinctBeginsWith = (
        "ValidationException",
        "Invalid FilterExpression: The first operand must be distinct from the remaining operands " +
        "for this operator or function; operator: begins_with, first operand: [info, label]");

    private static readonly (string Code, string Message) NotDistinctAttributeType = (
        "ValidationException",
        "Invalid FilterExpression: The first operand must be distinct from the remaining operands " +
        "for this operator or function; operator: attribute_type, first operand: [info, label]");

    /// <summary>(code, message) of the error fn() raises; raises if the call unexpectedly succeeds.</summary>
    private static async Task<(string? Code, string? Message)> Error<T>(Func<Task<T>> fn)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is null)
        {
            throw Fail($"expected the call to be rejected, but it succeeded: {result}");
        }

        return (error.Code, error.Message);
    }

    private static async Task ExpectError<T>(Func<Task<T>> fn, (string Code, string Message) expected)
        where T : class
    {
        var (gotCode, gotMessage) = await Error(fn);
        Ensure(gotCode == expected.Code, $"expected {Py.Repr(expected.Code)}, got {Py.Repr(gotCode)} ({Py.Repr(gotMessage)})");
        Ensure(gotMessage == expected.Message, $"unexpected message {Py.Repr(gotMessage)}");
    }

    /// <summary>
    /// A Scan FilterExpression naming the same nested document path as both operands of
    /// begins_with or attribute_type is refused at parse time, through a bare path or an
    /// alias; either function with a distinct second operand still evaluates.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.scan-filter-expression-non-distinct-function-nested-path-operands-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-ddbfnp-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            Tags = [new Tag { Key = "sim2real", Value = Check }],
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(table));
        await ddb.WaitForTableExists(table, delaySeconds: 2, maxAttempts: 250);
        await ddb.PutItemAsync(table, Item(("pk", S("item1")), ("info", M(("label", S("hello"))))));

        await ExpectError(
            () => ddb.ScanAsync(new ScanRequest
            {
                TableName = table,
                ConsistentRead = true,
                FilterExpression = "begins_with(info.label, info.label)",
            }),
            NotDistinctBeginsWith);
        await ExpectError(
            () => ddb.ScanAsync(new ScanRequest
            {
                TableName = table,
                ConsistentRead = true,
                FilterExpression = "attribute_type(info.label, info.label)",
            }),
            NotDistinctAttributeType);

        // Aliasing both path segments still resolves to the same document path in the message.
        await ExpectError(
            () => ddb.ScanAsync(new ScanRequest
            {
                TableName = table,
                ConsistentRead = true,
                FilterExpression = "begins_with(#i.#l, #i.#l)",
                ExpressionAttributeNames = Names(("#i", "info"), ("#l", "label")),
            }),
            NotDistinctBeginsWith);

        var controlBeginsWith = await ddb.ScanAsync(new ScanRequest
        {
            TableName = table,
            ConsistentRead = true,
            FilterExpression = "begins_with(info.label, :p)",
            ExpressionAttributeValues = Item((":p", S("hel"))),
        });
        Ensure(
            controlBeginsWith.Count == 1,
            $"expected 1 item matching begins_with(info.label, 'hel'), got {controlBeginsWith.Count}");
        Ensure(
            Py.Equal(controlBeginsWith.Items[0]["pk"], S("item1")),
            $"expected item1 to match, got {Py.Repr(controlBeginsWith.Items[0].GetValueOrDefault("pk"))}");

        var controlAttributeType = await ddb.ScanAsync(new ScanRequest
        {
            TableName = table,
            ConsistentRead = true,
            FilterExpression = "attribute_type(info.label, :t)",
            ExpressionAttributeValues = Item((":t", S("S"))),
        });
        Ensure(
            controlAttributeType.Count == 1,
            $"expected 1 item matching attribute_type(info.label, 'S'), got {controlAttributeType.Count}");
        Ensure(
            Py.Equal(controlAttributeType.Items[0]["pk"], S("item1")),
            $"expected item1 to match, got {Py.Repr(controlAttributeType.Items[0].GetValueOrDefault("pk"))}");
    }
}
