// Translated from behavioral/dynamodb/check_update_expression_malformed_syntax_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Ddb;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.update-expression-malformed-syntax-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class UpdateExpressionMalformedSyntaxRejected
{
    private const string CheckId = "update-expression-malformed-syntax-rejected";

    /// <summary>
    /// An UpdateExpression with unparsable grammar (a dangling operator or redundant
    /// parentheses) is rejected with ValidationException before any mutation occurs, while
    /// parenthesised arithmetic and a plain assignment on the same item still succeed.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.update-expression-malformed-syntax-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var tableName = FidelityContext.UniqueName("sim2real-behav-ue-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = tableName,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real-behavioral-check", Value = CheckId }],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(tableName));
        await ddb.WaitForTableExists(tableName, delaySeconds: 2, maxAttempts: 250);

        var key = Item(("pk", S("item-1")));
        await ddb.PutItemAsync(tableName, Item(("pk", S("item-1")), ("foo", S("original"))));

        var err = await Error(() => ddb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = tableName,
            Key = key,
            UpdateExpression = "SET foo =",
        }));
        Ensure(err.Code == "ValidationException", $"expected ValidationException, got {ErrorRepr(err)}");
        const string expected = "1 validation error detected: Invalid UpdateExpression: Syntax error; token: \"<EOF>\", near: \"=\"";
        Ensure(err.Message == expected, $"expected message {Py.Repr(expected)}, got {Py.Repr(err.Message)}");

        // Effect absent: the item's attribute was not touched by the rejected call.
        var item = (await ddb.GetItemAsync(new GetItemRequest { TableName = tableName, Key = key, ConsistentRead = true })).Item;
        Ensure(item["foo"].S == "original", $"expected 'original' (untouched), got {Py.Repr(item["foo"].S)}");

        // Positive control: a syntactically valid UpdateExpression on the same item succeeds.
        await ddb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = tableName,
            Key = key,
            UpdateExpression = "SET foo = :v",
            ExpressionAttributeValues = Item((":v", S("updated"))),
        });
        item = (await ddb.GetItemAsync(new GetItemRequest { TableName = tableName, Key = key, ConsistentRead = true })).Item;
        Ensure(item["foo"].S == "updated", $"expected 'updated', got {Py.Repr(item["foo"].S)}");

        // Parenthesised arithmetic is accepted: parentheses may group an operand pair.
        await ddb.PutItemAsync(tableName, Item(("pk", S("item-1")), ("n", N("5"))));
        await ddb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = tableName,
            Key = key,
            UpdateExpression = "SET n = (:a + :b)",
            ExpressionAttributeValues = Item((":a", N("1")), (":b", N("2"))),
        });
        item = (await ddb.GetItemAsync(new GetItemRequest { TableName = tableName, Key = key, ConsistentRead = true })).Item;
        Ensure(item["n"].N == "3", $"expected '3' from (:a + :b), got {Py.Repr(item["n"].N)}");

        // Redundant (doubled) parentheses around a single operand are rejected, before any mutation.
        err = await Error(() => ddb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = tableName,
            Key = key,
            UpdateExpression = "SET n = ((:v))",
            ExpressionAttributeValues = Item((":v", N("99"))),
        }));
        Ensure(err.Code == "ValidationException", $"expected ValidationException, got {ErrorRepr(err)}");
        const string expectedRedundant = "1 validation error detected: Invalid UpdateExpression: The expression has redundant parentheses;";
        Ensure(err.Message == expectedRedundant, $"expected message {Py.Repr(expectedRedundant)}, got {Py.Repr(err.Message)}");
        item = (await ddb.GetItemAsync(new GetItemRequest { TableName = tableName, Key = key, ConsistentRead = true })).Item;
        Ensure(item["n"].N == "3", $"expected '3' (untouched by the rejected call), got {Py.Repr(item["n"].N)}");
    }

    /// <summary>Calls <paramref name="fn"/>; returns its error. An unexpected success fails the check.</summary>
    private static async Task<AwsError> Error<T>(Func<Task<T>> fn)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        return error ?? throw Fail($"real AWS rejects this UpdateExpression, but it SUCCEEDED and returned {Py.Repr(result)}");
    }

    private static string ErrorRepr(AwsError error) =>
        Py.Repr(new Dictionary<string, object?> { ["Message"] = error.Message, ["Code"] = error.Code });
}
