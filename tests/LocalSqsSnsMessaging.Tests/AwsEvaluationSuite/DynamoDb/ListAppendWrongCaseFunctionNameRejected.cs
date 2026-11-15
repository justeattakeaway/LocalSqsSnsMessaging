// Translated from behavioral/dynamodb/check_list_append_wrong_case_function_name_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Ddb;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.list-append-wrong-case-function-name-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class ListAppendWrongCaseFunctionNameRejected
{
    private const string CheckId = "list-append-wrong-case-function-name-rejected";

    /// <summary>(code, message) of the ClientError `fn()` raises; RAISES if the call unexpectedly succeeds.</summary>
    private static async Task<(string? Code, string? Message)> Error<T>(Func<Task<T>> fn)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is not null)
        {
            return (error.Code, error.Message);
        }

        throw Fail($"real AWS rejects this call, but it SUCCEEDED and returned {Py.Repr(result)}");
    }

    /// <summary>
    /// UpdateItem's expression functions are case-sensitive: `LIST_APPEND` is an unrecognized
    /// function name and is refused, leaving the list untouched; `list_append` still applies.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.list-append-wrong-case-function-name-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real", Value = CheckId }],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(table));
        await ddb.WaitForTableExists(table, delaySeconds: 2, maxAttempts: 250);
        await ddb.PutItemAsync(table, Item(("pk", S("x")), ("l", L(S("a")))));

        var (code, message) = await Error(() => ddb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = table,
            Key = Item(("pk", S("x"))),
            UpdateExpression = "SET #l = LIST_APPEND(#l, :v)",
            ExpressionAttributeNames = Names(("#l", "l")),
            ExpressionAttributeValues = Item((":v", L(S("b")))),
        }));
        const string expected = "1 validation error detected: Invalid UpdateExpression: Invalid function name; function: LIST_APPEND";
        Ensure(
            (code, message) == ("ValidationException", expected),
            $"SET with LIST_APPEND (wrong case) raised {Py.Repr((code, message))}, expected {Py.Repr(("ValidationException", expected))}");

        var unchanged = (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Item(("pk", S("x"))), ConsistentRead = true })).Item;
        Ensure(Py.Equal(unchanged["l"].L, new[] { S("a") }), $"the rejected LIST_APPEND must not change the list, got {Py.Repr(unchanged["l"])}");

        await ddb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = table,
            Key = Item(("pk", S("x"))),
            UpdateExpression = "SET #l = list_append(#l, :v)",
            ExpressionAttributeNames = Names(("#l", "l")),
            ExpressionAttributeValues = Item((":v", L(S("b")))),
        });
        var grown = (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Item(("pk", S("x"))), ConsistentRead = true })).Item;
        Ensure(Py.Equal(grown["l"].L, new[] { S("a"), S("b") }), $"the positive-control list_append (correct case) must append, got {Py.Repr(grown["l"])}");
    }
}
