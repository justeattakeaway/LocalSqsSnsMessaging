// Translated from behavioral/dynamodb/check_query_filter_expression_redundant_parentheses_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Ddb;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.query-filter-expression-redundant-parentheses-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class QueryFilterExpressionRedundantParenthesesRejected
{
    private const string Check = "query-filter-expression-redundant-parentheses-rejected";

    private static readonly (string Code, string Message) RedundantParens =
        ("ValidationException", "Invalid FilterExpression: The expression has redundant parentheses;");

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

    /// <summary>
    /// A Query FilterExpression doubly wrapping the same clause in parentheses is
    /// rejected as redundant, both around a comparison and around a function call, while
    /// a single wrap of either form still evaluates.
    ///
    /// Reference: https://docs.aws.amazon.com/amazondynamodb/latest/developerguide/Expressions.OperatorsAndFunctions.html
    /// </summary>
    [Test]
    [DisplayName("dynamodb.query-filter-expression-redundant-parentheses-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-ddbqrp-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            Tags = [new Tag { Key = "sim2real", Value = Check }],
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH), new KeySchemaElement("sk", KeyType.RANGE)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S), new AttributeDefinition("sk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(table));
        await ddb.WaitForTableExists(table, delaySeconds: 2, maxAttempts: 250);
        await ddb.PutItemAsync(table, Item(("pk", S("grp")), ("sk", S("a")), ("category", S("A"))));

        QueryRequest Request(string filterExpression) => new()
        {
            TableName = table,
            ConsistentRead = true,
            KeyConditionExpression = "pk = :p",
            FilterExpression = filterExpression,
            ExpressionAttributeValues = Item((":p", S("grp")), (":c", S("A"))),
        };

        await ExpectError(() => ddb.QueryAsync(Request("((category = :c))")), RedundantParens);
        await ExpectError(() => ddb.QueryAsync(Request("((begins_with(category, :c)))")), RedundantParens);

        var control = await ddb.QueryAsync(Request("(category = :c)"));
        Ensure(control.Count == 1, $"the single-wrapped equivalent returned Count={control.Count}, expected 1");
        Ensure(
            Py.Equal(control.Items[0]["sk"], S("a")),
            $"the single-wrapped equivalent returned sk={Py.Repr(control.Items[0].GetValueOrDefault("sk"))}, expected 'a'");
    }
}
