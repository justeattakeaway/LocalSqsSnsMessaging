// Translated from behavioral/dynamodb/check_scan_filter_expression_redundant_nested_parens_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.scan-filter-expression-redundant-nested-parens-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class ScanFilterExpressionRedundantNestedParensRejected
{
    private const string Check = "scan-filter-expression-redundant-nested-parens-rejected";

    private static readonly (string Code, string Message) Redundant = ("ValidationException", "Invalid FilterExpression: The expression has redundant parentheses;");

    private static readonly (string Id, string Status, string Category)[] Rows =
    [
        ("item-001", "active", "premium"),
        ("item-002", "inactive", "basic"),
    ];

    /// <summary>(code, message) of the ClientError fn() raises; raises if the call unexpectedly succeeds.</summary>
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

    private static Dictionary<string, AttributeValue> RowItem((string Id, string Status, string Category) row) =>
        Item(("id", S(row.Id)), ("status", S(row.Status)), ("category", S(row.Category)));

    /// <summary>A fresh id-keyed table seeded with two records.</summary>
    private static async Task<string> Seed(FidelityContext ctx)
    {
        var ddb = ctx.Dynamo;
        var name = FidelityContext.UniqueName("sim2real-ddbparenrej-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = name,
            Tags = [new Tag { Key = "sim2real", Value = Check }],
            KeySchema = [new KeySchemaElement("id", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("id", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(name));
        await ddb.WaitForTableExists(name, delaySeconds: 2, maxAttempts: 250);
        var resp = await ddb.BatchWriteItemAsync(new BatchWriteItemRequest
        {
            RequestItems = new() { [name] = [.. Rows.Select(r => new WriteRequest { PutRequest = new PutRequest { Item = RowItem(r) } })] },
        });
        Ensure((resp.UnprocessedItems ?? []).Count == 0, $"seeding BatchWriteItem left {Py.Repr(resp.UnprocessedItems)} unprocessed");
        return name;
    }

    /// <summary>
    /// A Scan FilterExpression wrapping a single comparison, or a NOT comparison, in a
    /// redundant extra pair of parentheses is rejected; a single pair of parens is accepted.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.scan-filter-expression-redundant-nested-parens-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var name = await Seed(ctx);
        await ExpectError(
            () => ddb.ScanAsync(new ScanRequest
            {
                TableName = name,
                FilterExpression = "((#c = :b))",
                ExpressionAttributeNames = Names(("#c", "category")),
                ExpressionAttributeValues = Item((":b", S("basic"))),
            }),
            Redundant);
        await ExpectError(
            () => ddb.ScanAsync(new ScanRequest
            {
                TableName = name,
                FilterExpression = "((NOT #s = :a))",
                ExpressionAttributeNames = Names(("#s", "status")),
                ExpressionAttributeValues = Item((":a", S("active"))),
            }),
            Redundant);
        await ExpectError(
            () => ddb.ScanAsync(new ScanRequest
            {
                TableName = name,
                FilterExpression = "((#s = :a)) AND #c = :b",
                ExpressionAttributeNames = Names(("#c", "category"), ("#s", "status")),
                ExpressionAttributeValues = Item((":b", S("basic")), (":a", S("active"))),
            }),
            Redundant);
        var control = await ddb.ScanAsync(new ScanRequest
        {
            TableName = name,
            FilterExpression = "(#c = :b)",
            ConsistentRead = true,
            ExpressionAttributeNames = Names(("#c", "category")),
            ExpressionAttributeValues = Item((":b", S("basic"))),
        });
        var ids = control.Items.Select(i => i["id"].S).Order(StringComparer.Ordinal).ToList();
        var expected = Rows.Where(r => r.Category == "basic").Select(r => r.Id).Order(StringComparer.Ordinal).ToList();
        Ensure(
            ids.SequenceEqual(expected),
            $"the single-paren control '(#c = :b)' returned {Py.Repr(ids)}, expected {Py.Repr(expected)}: a single pair of parens is accepted, only the redundant extra pair is rejected");
    }
}
