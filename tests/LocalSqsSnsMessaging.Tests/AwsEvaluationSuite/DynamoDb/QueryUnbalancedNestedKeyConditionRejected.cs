// Translated from behavioral/dynamodb/check_query_unbalanced_nested_key_condition_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Ddb;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.query-unbalanced-nested-key-condition-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class QueryUnbalancedNestedKeyConditionRejected
{
    private const string Check = "query-unbalanced-nested-key-condition-rejected";

    private static readonly (string Code, string Message) SyntaxError =
        ("ValidationException", "Invalid KeyConditionExpression: Syntax error; token: \"<EOF>\", near: \")\"");

    /// <summary>Raises on an unexpected success; otherwise asserts the exact (code, message).</summary>
    private static async Task ExpectError<T>(Func<Task<T>> fn, (string Code, string Message) expected)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is not null)
        {
            Ensure(error.Code == expected.Code, $"expected code {Py.Repr(expected.Code)}, got {Py.Repr(error.Code)}");
            Ensure(error.Message == expected.Message, $"expected message {Py.Repr(expected.Message)}, got {Py.Repr(error.Message)}");
            return;
        }

        throw Fail($"expected the call to be refused with {expected.Code}, but it succeeded: {result}");
    }

    /// <summary>
    /// A Query KeyConditionExpression whose nested parentheses are unbalanced is refused
    /// with a ValidationException naming the syntax error; the balanced form returns the item.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.query-unbalanced-nested-key-condition-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var t = FidelityContext.UniqueName("sim2real-behav-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = t,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH), new KeySchemaElement("sk", KeyType.RANGE)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S), new AttributeDefinition("sk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real", Value = Check }],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(t));
        await ddb.WaitForTableExists(t, delaySeconds: 2, maxAttempts: 250);
        await ddb.PutItemAsync(t, Item(("pk", S("p1")), ("sk", S("s1"))));

        QueryRequest Request(string kce) => new()
        {
            TableName = t,
            KeyConditionExpression = kce,
            ExpressionAttributeValues = Item((":p", S("p1")), (":s", S("s1"))),
            ConsistentRead = true,
        };

        // Positive control: the balanced nested form (inner paren wraps just the sort-key
        // clause) is accepted, so the rejection below is shown to be about the missing
        // closing paren, not about nesting in general.
        var control = await ddb.QueryAsync(Request("(pk = :p AND (sk = :s))"));
        Ensure(control.Count == 1, $"positive control: expected 1 item, got {control.Count}");

        // The inner paren around the sort-key clause is never closed.
        await ExpectError(() => ddb.QueryAsync(Request("(pk = :p AND (sk = :s)")), SyntaxError);

        // Final control: the balanced nested form still works after the rejection above.
        var controlAfter = await ddb.QueryAsync(Request("(pk = :p AND (sk = :s))"));
        Ensure(controlAfter.Count == 1, $"positive control after rejection: expected 1 item, got {controlAfter.Count}");
    }
}
