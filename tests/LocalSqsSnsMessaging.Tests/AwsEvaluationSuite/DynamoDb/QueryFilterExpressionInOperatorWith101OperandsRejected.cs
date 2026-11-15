// Translated from behavioral/dynamodb/check_query_filter_expression_in_operator_with_101_operands_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Ddb;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.query-filter-expression-in-operator-with-101-operands-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class QueryFilterExpressionInOperatorWith101OperandsRejected
{
    private const string Check = "query-filter-expression-in-operator-with-101-operands-rejected";

    private static readonly (string Sk, string Status)[] Rows =
    [
        ("o1", "pending"),
        ("o2", "complete"),
        ("o3", "pending"),
    ];

    /// <summary>
    /// A Query FilterExpression's IN operator rejects 101 operands with an exact
    /// ValidationException message; 100 operands is the accepted boundary and still matches.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.query-filter-expression-in-operator-with-101-operands-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-ddbqin-");
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
        foreach (var (sk, status) in Rows)
        {
            await ddb.PutItemAsync(table, Item(("pk", S("u1")), ("sk", S(sk)), ("status", S(status))));
        }

        const int operandCount = 101;
        var values = Item((":pk", S("u1")));
        for (var i = 0; i < operandCount; i++)
        {
            values[$":v{i}"] = S($"x{i}");
        }

        var (code, message) = await ErrorCode(() => ddb.QueryAsync(new QueryRequest
        {
            TableName = table,
            KeyConditionExpression = "pk = :pk",
            FilterExpression = "#s IN (" + string.Join(", ", Enumerable.Range(0, operandCount).Select(i => $":v{i}")) + ")",
            ExpressionAttributeNames = Names(("#s", "status")),
            ExpressionAttributeValues = values,
        }));
        const string expectedCode = "ValidationException";
        var expectedMessage =
            "Invalid FilterExpression: The IN operator is provided with too many operands; " +
            $"number of operands: {operandCount}";
        Ensure(code == expectedCode, $"the 101-operand IN raised {Py.Repr(code)}, expected {Py.Repr(expectedCode)}");
        Ensure(message == expectedMessage, $"the 101-operand IN message was {Py.Repr(message)}, expected {Py.Repr(expectedMessage)}");

        // Positive control: 100 operands, including the value that actually matches, still answers.
        var vals = new Dictionary<string, AttributeValue>(StringComparer.Ordinal);
        for (var i = 0; i < 100; i++)
        {
            vals[$":v{i}"] = S(i == 0 ? "pending" : $"x{i}");
        }

        var allValues = Item((":pk", S("u1")));
        foreach (var (k, v) in vals)
        {
            allValues[k] = v;
        }

        var ok = await ddb.QueryAsync(new QueryRequest
        {
            TableName = table,
            KeyConditionExpression = "pk = :pk",
            FilterExpression = "#s IN (" + string.Join(", ", vals.Keys) + ")",
            ExpressionAttributeNames = Names(("#s", "status")),
            ExpressionAttributeValues = allValues,
            ConsistentRead = true,
        });
        var want = Rows.Count(r => r.Status == "pending");
        Ensure(ok.Count == want, $"the 100-operand IN matched Count={Py.Repr(ok.Count)}, expected {Py.Repr(want)}");
    }

    /// <summary>The AWS (code, message) raised by fn(); raises if fn() unexpectedly succeeds.</summary>
    private static async Task<(string? Code, string? Message)> ErrorCode(Func<Task> fn)
    {
        var error = await FidelityContext.CaptureError(fn);
        if (error is null)
        {
            throw Fail("expected the call to be rejected, but it succeeded");
        }

        return (error.Code, error.Message);
    }
}
