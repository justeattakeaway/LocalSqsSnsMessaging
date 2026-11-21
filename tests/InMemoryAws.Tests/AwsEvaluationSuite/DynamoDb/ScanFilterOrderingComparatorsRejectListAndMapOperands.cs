// Translated from behavioral/dynamodb/check_scan_filter_ordering_comparators_reject_list_and_map_operands.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit abd0811).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.scan-filter-ordering-comparators-reject-list-and-map-operands</summary>
[Category("AwsEvaluationSuite")]
public sealed class ScanFilterOrderingComparatorsRejectListAndMapOperands
{
    private const string Check = "scan-filter-ordering-comparators-reject-list-and-map-operands";

    private static AttributeValue ListVal() => L(S("x"));

    private static AttributeValue MapVal() => M(("a", S("1")));

    /// <summary>Every ordering comparator the FilterExpression grammar supports besides = and &lt;>.</summary>
    private static readonly string[] OrderingOps = ["<", "<=", ">", ">="];

    /// <summary>An on-demand table keyed by pk (S) HASH only.</summary>
    private static async Task<string> MakeTable(FidelityContext ctx)
    {
        var ddb = ctx.Dynamo;
        var name = FidelityContext.UniqueName("sim2real-behav-ddbcmp-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = name,
            Tags = [new Tag { Key = "sim2real", Value = Check }],
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(name));
        await ddb.WaitForTableExists(name, delaySeconds: 2, maxAttempts: 90);
        return name;
    }

    /// <summary><c>(code, message)</c> fn() raises. Raises when the call SUCCEEDS: real AWS rejects it.</summary>
    private static async Task<(string? Code, string? Message)> ErrorCode<T>(Func<Task<T>> fn)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is null)
        {
            throw Fail($"expected the call to be refused, but it succeeded: {result}");
        }

        return (error.Code, error.Message);
    }

    private static async Task<int?> EqCount(IAmazonDynamoDB ddb, string name, AttributeValue val) =>
        (await ddb.ScanAsync(new ScanRequest
        {
            TableName = name,
            FilterExpression = "val = :v",
            ExpressionAttributeValues = Item((":v", val)),
            ConsistentRead = true,
            Select = Select.COUNT,
        })).Count;

    /// <summary>
    /// Scan's FilterExpression refuses &lt;, &lt;=, > and >= on a List operand and > on a Map operand
    /// with the operand-type ValidationException naming the operator and the type, while = on the
    /// List operand matches the same item before and after the refusals.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.scan-filter-ordering-comparators-reject-list-and-map-operands")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var name = await MakeTable(ctx);
        await ddb.PutItemAsync(name, Item(("pk", S("item-l")), ("val", ListVal())));
        await ddb.PutItemAsync(name, Item(("pk", S("item-m")), ("val", MapVal())));

        // Positive control, taken before any rejection: equality matches the List attribute exactly.
        var before = await EqCount(ddb, name, ListVal());
        Ensure(before == 1, $"'val = :v' on the List operand matched {before}, expected 1");

        foreach (var op in OrderingOps)
        {
            var (code, message) = await ErrorCode(() => ddb.ScanAsync(new ScanRequest
            {
                TableName = name,
                FilterExpression = $"val {op} :v",
                ExpressionAttributeValues = Item((":v", ListVal())),
                ConsistentRead = true,
            }));
            Ensure(code == "ValidationException", $"'val {op} :v' on a List raised {Py.Repr(code)} ({Py.Repr(message)})");
            var expected =
                "Invalid FilterExpression: Incorrect operand type for operator or function; " +
                $"operator or function: {op}, operand type: L";
            Ensure(message == expected, $"'val {op} :v' on a List raised message {Py.Repr(message)}, expected {Py.Repr(expected)}");
        }

        // The same ordering comparator refuses a Map operand too -- it is not List-specific.
        var (codeM, messageM) = await ErrorCode(() => ddb.ScanAsync(new ScanRequest
        {
            TableName = name,
            FilterExpression = "val > :v",
            ExpressionAttributeValues = Item((":v", MapVal())),
            ConsistentRead = true,
        }));
        Ensure(codeM == "ValidationException", $"'val > :v' on a Map raised {Py.Repr(codeM)} ({Py.Repr(messageM)})");
        const string expectedM =
            "Invalid FilterExpression: Incorrect operand type for operator or function; " +
            "operator or function: >, operand type: M";
        Ensure(messageM == expectedM, $"'val > :v' on a Map raised message {Py.Repr(messageM)}, expected {Py.Repr(expectedM)}");

        // The effect is absent and the table is unaffected: the same equality control still matches.
        var after = await EqCount(ddb, name, ListVal());
        Ensure(after == before, $"the equality control matched {after} after the rejections, expected {before}");
    }
}
