// Translated from behavioral/dynamodb/check_update_item_nested_function_operand_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit abd0811).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.update-item-nested-function-operand-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class UpdateItemNestedFunctionOperandRejected
{
    private const string Check = "update-item-nested-function-operand-rejected";

    /// <summary>(code, message) of the error <c>fn()</c> raises; RAISES if the call unexpectedly succeeds.</summary>
    private static async Task<(string? Code, string? Message)> Error(Func<Task<object>> fn)
    {
        var (result, err) = await FidelityContext.Capture(fn);
        return err is null ? throw Fail($"real AWS rejects this call, but it SUCCEEDED and returned {result}") : (err.Code, err.Message);
    }

    /// <summary>
    /// UpdateItem rejects list_append nested inside list_append, and if_not_exists given a
    /// nested if_not_exists instead of a document path as its first operand; if_not_exists
    /// nested as list_append's first operand still applies.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.update-item-nested-function-operand-rejected")]
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
            Tags = [new Tag { Key = "sim2real", Value = Check }],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(table));
        await ddb.WaitForTableExists(table, delaySeconds: 2, maxAttempts: 250);
        await ddb.PutItemAsync(table, Item(("pk", S("x")), ("l", L(N("1")))));
        var key = Item(("pk", S("x")));

        async Task<Dictionary<string, AttributeValue>> Read() =>
            (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = key, ConsistentRead = true })).Item;

        const string nestedPrefix = "1 validation error detected: Invalid UpdateExpression: ";

        var (code, message) = await Error(async () => await ddb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = table,
            Key = key,
            UpdateExpression = "SET #l = list_append(list_append(#l, :a), :b)",
            ExpressionAttributeNames = Names(("#l", "l")),
            ExpressionAttributeValues = Item((":a", L(N("2"))), (":b", L(N("3")))),
        }));
        const string expectedNestedListAppend =
            nestedPrefix + "The function is not allowed to be used this way in an expression; " +
            "function: list_append";
        Ensure(
            (code, message) == ("ValidationException", expectedNestedListAppend),
            $"list_append nested inside list_append raised {Py.Repr((code, message))}, expected " +
            $"{Py.Repr(("ValidationException", expectedNestedListAppend))}");

        var unchanged = await Read();
        Ensure(
            Py.Equal(unchanged["l"].L, new[] { N("1") }),
            $"the rejected nested list_append must not change the list, got {Py.Repr(unchanged["l"])}");

        (code, message) = await Error(async () => await ddb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = table,
            Key = key,
            UpdateExpression = "SET #l = if_not_exists(if_not_exists(#l, :a), :b)",
            ExpressionAttributeNames = Names(("#l", "l")),
            ExpressionAttributeValues = Item((":a", L(N("2"))), (":b", L(N("3")))),
        }));
        const string expectedNestedIfNotExists =
            nestedPrefix + "Operator or function requires a document path; " +
            "operator or function: if_not_exists";
        Ensure(
            (code, message) == ("ValidationException", expectedNestedIfNotExists),
            $"if_not_exists nested as its own first operand raised {Py.Repr((code, message))}, expected " +
            $"{Py.Repr(("ValidationException", expectedNestedIfNotExists))}");

        var stillUnchanged = await Read();
        Ensure(
            Py.Equal(stillUnchanged["l"].L, new[] { N("1") }),
            $"the rejected nested if_not_exists must not change the list, got {Py.Repr(stillUnchanged["l"])}");

        await ddb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = table,
            Key = key,
            UpdateExpression = "SET #m = list_append(if_not_exists(#m, :empty), :c)",
            ExpressionAttributeNames = Names(("#m", "m")),
            ExpressionAttributeValues = Item((":empty", L()), (":c", L(N("9")))),
        });
        var grown = await Read();
        Ensure(
            Py.Equal(grown["m"].L, new[] { N("9") }),
            $"list_append nested inside if_not_exists's own first operand must apply, got {Py.Repr(grown["m"])}");
    }
}
