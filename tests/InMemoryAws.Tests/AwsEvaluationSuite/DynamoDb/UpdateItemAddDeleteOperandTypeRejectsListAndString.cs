// Translated from behavioral/dynamodb/check_update_item_add_delete_operand_type_rejects_list_and_string.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit abd0811).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.update-item-add-delete-operand-type-rejects-list-and-string</summary>
[Category("AwsEvaluationSuite")]
public sealed class UpdateItemAddDeleteOperandTypeRejectsListAndString
{
    private const string Check = "update-item-add-delete-operand-type-rejects-list-and-string";

    private static readonly (string Code, string Message) AddList = (
        "ValidationException",
        "1 validation error detected: Invalid UpdateExpression: Incorrect operand type for " +
        "operator or function; operator: ADD, operand type: LIST, typeSet: ALLOWED_FOR_ADD_OPERAND");

    private static readonly (string Code, string Message) DeleteString = (
        "ValidationException",
        "1 validation error detected: Invalid UpdateExpression: Incorrect operand type for " +
        "operator or function; operator: DELETE, operand type: STRING, typeSet: ALLOWED_FOR_ADD_OPERAND");

    /// <summary>Assert <c>fn()</c> raises exactly <c>(code, message)</c>; raises on success.</summary>
    private static async Task ExpectError(Func<Task<object>> fn, (string Code, string Message) expected)
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is not null)
        {
            var got = (error.Code, error.Message);
            Ensure(got == expected, $"expected {Py.Repr(expected)}, got {Py.Repr(got)}");
            return;
        }

        throw Fail($"real AWS rejects this call, but it SUCCEEDED and returned {result}");
    }

    /// <summary>
    /// UpdateItem's ADD rejects a List operand and DELETE rejects a String operand with the
    /// operator's typeSet error, on a fresh attribute and on one that already holds another type.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.update-item-add-delete-operand-type-rejects-list-and-string")]
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

        var key = Item(("pk", S("x")));
        await ddb.PutItemAsync(table, Item(("pk", S("x")), ("count", N("5")), ("tags", SS("a", "b"))));
        async Task<Dictionary<string, AttributeValue>> Get() =>
            (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = key, ConsistentRead = true })).Item;

        // 1. ADD with a List operand onto an attribute that does not exist yet.
        await ExpectError(
            async () => await ddb.UpdateItemAsync(new UpdateItemRequest
            {
                TableName = table,
                Key = key,
                UpdateExpression = "ADD freshList :v",
                ExpressionAttributeValues = Item((":v", L(S("z")))),
            }),
            AddList);
        var after1 = await Get();
        Ensure(!after1.ContainsKey("freshList"), $"the rejected ADD must not create the attribute, got {Py.Repr(after1)}");

        // 2. DELETE with a String operand onto an attribute that does not exist yet.
        await ExpectError(
            async () => await ddb.UpdateItemAsync(new UpdateItemRequest
            {
                TableName = table,
                Key = key,
                UpdateExpression = "DELETE freshStr :v",
                ExpressionAttributeValues = Item((":v", S("z"))),
            }),
            DeleteString);
        var after2 = await Get();
        Ensure(!after2.ContainsKey("freshStr"), $"the rejected DELETE must not create the attribute, got {Py.Repr(after2)}");

        // 3. ADD with a List operand onto an attribute that already holds a Number.
        await ExpectError(
            async () => await ddb.UpdateItemAsync(new UpdateItemRequest
            {
                TableName = table,
                Key = key,
                UpdateExpression = "ADD #c :v",
                ExpressionAttributeNames = Names(("#c", "count")),
                ExpressionAttributeValues = Item((":v", L(S("z")))),
            }),
            AddList);
        var after3 = await Get();
        Ensure(Py.Equal(after3["count"], N("5")), $"the rejected ADD must leave count unchanged, got {Py.Repr(after3["count"])}");

        // 4. DELETE with a String operand onto an attribute that already holds a String Set.
        await ExpectError(
            async () => await ddb.UpdateItemAsync(new UpdateItemRequest
            {
                TableName = table,
                Key = key,
                UpdateExpression = "DELETE #t :v",
                ExpressionAttributeNames = Names(("#t", "tags")),
                ExpressionAttributeValues = Item((":v", S("z"))),
            }),
            DeleteString);
        var after4 = await Get();
        Ensure(
            new HashSet<string>(after4["tags"].SS, StringComparer.Ordinal).SetEquals(["a", "b"]),
            $"the rejected DELETE must leave tags unchanged, got {Py.Repr(after4["tags"])}");

        // Positive controls: same-typed operands still apply.
        await ddb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = table,
            Key = key,
            UpdateExpression = "ADD #c :v",
            ExpressionAttributeNames = Names(("#c", "count")),
            ExpressionAttributeValues = Item((":v", N("3"))),
        });
        var grown = await Get();
        Ensure(Py.Equal(grown["count"], N("8")), $"the positive-control ADD must apply, got {Py.Repr(grown["count"])}");

        await ddb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = table,
            Key = key,
            UpdateExpression = "DELETE #t :v",
            ExpressionAttributeNames = Names(("#t", "tags")),
            ExpressionAttributeValues = Item((":v", SS("a"))),
        });
        var shrunk = await Get();
        Ensure(
            new HashSet<string>(shrunk["tags"].SS, StringComparer.Ordinal).SetEquals(["b"]),
            $"the positive-control DELETE must apply, got {Py.Repr(shrunk["tags"])}");
    }
}
