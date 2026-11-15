// Translated from behavioral/dynamodb/check_scan_and_query_filter_ordering_comparison_on_a_set_attribute_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Ddb;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.scan-and-query-filter-ordering-comparison-on-a-set-attribute-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class ScanAndQueryFilterOrderingComparisonOnASetAttributeRejected
{
    private const string Check = "scan-and-query-filter-ordering-comparison-on-a-set-attribute-rejected";

    private static readonly string[] OrderingOps = ["<", ">"];

    private static string OperandTypeMsg(string op, string opType) =>
        "Invalid FilterExpression: Incorrect operand type for operator or function; " +
        $"operator or function: {op}, operand type: {opType}";

    private static (string Attr, string OpType, AttributeValue Operand)[] SetOperands() =>
    [
        ("ssattr", "SS", SS("a", "b")),
        ("nsattr", "NS", NS("1", "2")),
        ("bsattr", "BS", BS(Encoding.UTF8.GetBytes("x"), Encoding.UTF8.GetBytes("y"))),
    ];

    /// <summary>(code, message) of the ClientError <paramref name="fn"/> raises; raises if it unexpectedly succeeds.</summary>
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
    /// A Scan or Query FilterExpression's '&lt;' and '>' refuse an SS, NS or BS operand with
    /// ValidationException, and leave the item exactly as it was before the rejected calls.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.scan-and-query-filter-ordering-comparison-on-a-set-attribute-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-setord-");
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
        const string pk = "p1";
        Dictionary<string, AttributeValue> ItemValue() => Item(
            ("pk", S(pk)), ("sk", S("item1")),
            ("ssattr", SS("a", "b")), ("nsattr", NS("1", "2")), ("bsattr", BS(Encoding.UTF8.GetBytes("x"), Encoding.UTF8.GetBytes("y"))));
        var item = ItemValue();
        await ddb.PutItemAsync(table, ItemValue());
        foreach (var (attr, opType, operand) in SetOperands())
        {
            foreach (var op in OrderingOps)
            {
                var (code, message) = await Error(() => ddb.ScanAsync(new ScanRequest
                {
                    TableName = table,
                    FilterExpression = $"{attr} {op} :v",
                    ExpressionAttributeValues = Item((":v", operand)),
                    ConsistentRead = true,
                }));
                Ensure(code == "ValidationException", $"Scan '{attr} {op} :v' with a {opType} operand answered {Py.Repr(code)}, expected ValidationException");
                var expectedMessage = OperandTypeMsg(op, opType);
                Ensure(message == expectedMessage, $"Scan '{attr} {op} :v' with a {opType} operand said {Py.Repr(message)}, expected {Py.Repr(expectedMessage)}");
            }
        }

        // The same refusal applies to Query's FilterExpression, applied after the key condition.
        {
            var (code, message) = await Error(() => ddb.QueryAsync(new QueryRequest
            {
                TableName = table,
                KeyConditionExpression = "pk = :pk",
                FilterExpression = "ssattr < :v",
                ExpressionAttributeValues = Item((":pk", S(pk)), (":v", SS("a", "b"))),
                ConsistentRead = true,
            }));
            Ensure(code == "ValidationException", $"Query 'ssattr < :v' with an SS operand answered {Py.Repr(code)}, expected ValidationException");
            var expectedMessage = OperandTypeMsg("<", "SS");
            Ensure(message == expectedMessage, $"Query 'ssattr < :v' with an SS operand said {Py.Repr(message)}, expected {Py.Repr(expectedMessage)}");
        }

        // Positive control: the rejected calls wrote nothing, and the item reads back unchanged.
        var after = (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Item(("pk", S(pk)), ("sk", S("item1"))), ConsistentRead = true })).Item;
        Ensure(Py.Equal(after, item), $"the item changed after the rejected filter calls, got {Py.Repr(after)}, expected {Py.Repr(item)}");
    }
}
