// Translated from behavioral/dynamodb/check_scan_filter_expression_non_distinct_comparator_and_aliased_operands_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit abd0811).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.scan-filter-expression-non-distinct-comparator-and-aliased-operands-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class ScanFilterExpressionNonDistinctComparatorAndAliasedOperandsRejected
{
    private const string Check = "scan-filter-expression-non-distinct-comparator-and-aliased-operands-rejected";

    private static readonly (string Code, string Message) NotDistinctNeQty = (
        "ValidationException",
        "Invalid FilterExpression: The first operand must be distinct from the remaining operands " +
        "for this operator or function; operator: <>, first operand: [qty]");

    private static readonly (string Code, string Message) NotDistinctGtQty = (
        "ValidationException",
        "Invalid FilterExpression: The first operand must be distinct from the remaining operands " +
        "for this operator or function; operator: >, first operand: [qty]");

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
    /// A Scan FilterExpression naming the same attribute as both operands of &lt;&gt; or &gt; is
    /// refused at parse time, whether parenthesized under OR/AND or written through an
    /// alias; a FilterExpression comparing distinct operands still evaluates.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.scan-filter-expression-non-distinct-comparator-and-aliased-operands-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-ddbfnd-");
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
        await ddb.PutItemAsync(table, Item(("pk", S("item1")), ("qty", N("5")), ("category", S("A"))));
        await ddb.PutItemAsync(table, Item(("pk", S("item2")), ("qty", N("10")), ("category", S("B"))));

        await ExpectError(
            () => ddb.ScanAsync(new ScanRequest { TableName = table, ConsistentRead = true, FilterExpression = "qty <> qty" }),
            NotDistinctNeQty);
        await ExpectError(
            () => ddb.ScanAsync(new ScanRequest { TableName = table, ConsistentRead = true, FilterExpression = "qty > qty" }),
            NotDistinctGtQty);

        // Parenthesizing the non-distinct branch and combining it with a well-formed OR branch
        // does not shield it: the parser rejects it before either branch is evaluated.
        await ExpectError(
            () => ddb.ScanAsync(new ScanRequest
            {
                TableName = table,
                ConsistentRead = true,
                FilterExpression = "(category = :c) OR (qty > qty)",
                ExpressionAttributeValues = Item((":c", S("A"))),
            }),
            NotDistinctGtQty);

        // The alias resolves to its attribute name in the message, not the placeholder.
        await ExpectError(
            () => ddb.ScanAsync(new ScanRequest
            {
                TableName = table,
                ConsistentRead = true,
                FilterExpression = "#q <> #q",
                ExpressionAttributeNames = Names(("#q", "qty")),
            }),
            NotDistinctNeQty);

        var controlGt = await ddb.ScanAsync(new ScanRequest
        {
            TableName = table,
            ConsistentRead = true,
            FilterExpression = "qty > :v",
            ExpressionAttributeValues = Item((":v", N("7"))),
        });
        Ensure(controlGt.Count == 1, $"expected 1 item with qty>7, got {controlGt.Count}");
        Ensure(
            Py.Equal(controlGt.Items[0]["pk"], S("item2")),
            $"expected item2 to match qty>7, got {Py.Repr(controlGt.Items[0].GetValueOrDefault("pk"))}");

        var controlEq = await ddb.ScanAsync(new ScanRequest
        {
            TableName = table,
            ConsistentRead = true,
            FilterExpression = "category = :c",
            ExpressionAttributeValues = Item((":c", S("A"))),
        });
        Ensure(controlEq.Count == 1, $"expected 1 item with category=A, got {controlEq.Count}");
        Ensure(
            Py.Equal(controlEq.Items[0]["pk"], S("item1")),
            $"expected item1 to match category=A, got {Py.Repr(controlEq.Items[0].GetValueOrDefault("pk"))}");
    }
}
