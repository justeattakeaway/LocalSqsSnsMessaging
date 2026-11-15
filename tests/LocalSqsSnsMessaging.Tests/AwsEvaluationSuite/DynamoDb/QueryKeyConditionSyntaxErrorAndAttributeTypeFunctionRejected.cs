// Translated from behavioral/dynamodb/check_query_key_condition_syntax_error_and_attribute_type_function_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Ddb;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.query-key-condition-syntax-error-and-attribute-type-function-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class QueryKeyConditionSyntaxErrorAndAttributeTypeFunctionRejected
{
    private const string Check = "query-key-condition-syntax-error-and-attribute-type-function-rejected";
    private const string ErrValidation = "ValidationException";

    /// <summary>The (code, message) of the ClientError fn() raises. RAISES when fn() unexpectedly succeeds.</summary>
    private static async Task<(string? Code, string? Message)> Error<T>(Func<Task<T>> fn)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is null)
        {
            throw Fail($"expected a ClientError, but the call SUCCEEDED and returned {result}");
        }

        return (error.Code, error.Message);
    }

    /// <summary>
    /// A Query KeyConditionExpression rejects a grammar syntax error (a dangling AND, a stray
    /// double-equals) and the attribute_type function, each with its own exact message; the legal
    /// Query still succeeds.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.query-key-condition-syntax-error-and-attribute-type-function-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-qkcs-");
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

        await ddb.PutItemAsync(table, Item(("pk", S("p")), ("sk", S("a"))));

        // (1) A dangling AND with nothing after it -- a grammar syntax error, not a semantic one.
        var (code, message) = await Error(() => ddb.QueryAsync(new QueryRequest
        {
            TableName = table,
            KeyConditionExpression = "pk = :p AND",
            ExpressionAttributeValues = Item((":p", S("p"))),
        }));
        Ensure(code == ErrValidation, $"dangling AND answered {Py.Repr(code)} ({Py.Repr(message)}), expected {ErrValidation}");
        const string expectedDangling = "Invalid KeyConditionExpression: Syntax error; token: \"<EOF>\", near: \"AND\"";
        Ensure(message == expectedDangling, $"expected message {Py.Repr(expectedDangling)}, got {Py.Repr(message)}");

        // (2) A stray repeated operator -- a token the grammar never accepts there.
        var (code2, message2) = await Error(() => ddb.QueryAsync(new QueryRequest
        {
            TableName = table,
            KeyConditionExpression = "pk = = :p",
            ExpressionAttributeValues = Item((":p", S("p"))),
        }));
        Ensure(code2 == ErrValidation, $"stray '=' answered {Py.Repr(code2)} ({Py.Repr(message2)}), expected {ErrValidation}");
        const string expectedStray = "Invalid KeyConditionExpression: Syntax error; token: \"=\", near: \"= = :p\"";
        Ensure(message2 == expectedStray, $"expected message {Py.Repr(expectedStray)}, got {Py.Repr(message2)}");

        // (3) attribute_type is a function the condition-expression grammar supports generally, but
        // a KeyConditionExpression permits only begins_with and BETWEEN beyond a plain comparison;
        // AWS reports it the same way as any other operator a key condition does not allow.
        var (code3, message3) = await Error(() => ddb.QueryAsync(new QueryRequest
        {
            TableName = table,
            KeyConditionExpression = "attribute_type(pk, :t)",
            ExpressionAttributeValues = Item((":t", S("S"))),
        }));
        Ensure(code3 == ErrValidation, $"attribute_type() answered {Py.Repr(code3)} ({Py.Repr(message3)}), expected {ErrValidation}");
        const string expectedFn = "Invalid KeyConditionExpression: Invalid operator used in KeyConditionExpression";
        Ensure(message3 == expectedFn, $"expected message {Py.Repr(expectedFn)}, got {Py.Repr(message3)}");

        // Positive control: the legal form over the same partition still works after all three
        // rejections, and none of them reached the table.
        var control = await ddb.QueryAsync(new QueryRequest
        {
            TableName = table,
            KeyConditionExpression = "pk = :p",
            ExpressionAttributeValues = Item((":p", S("p"))),
            ConsistentRead = true,
        });
        Ensure(control.Count == 1, $"expected Count 1 for the legal Query, got {control.Count}");
        Ensure(
            Py.Equal(control.Items, new[] { Item(("pk", S("p")), ("sk", S("a"))) }),
            $"expected only the original item, got {Py.Repr(control.Items)}");
    }
}
