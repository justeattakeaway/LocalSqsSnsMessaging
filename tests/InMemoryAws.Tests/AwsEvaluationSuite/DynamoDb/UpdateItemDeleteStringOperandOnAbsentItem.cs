// Translated from behavioral/dynamodb/check_update_item_delete_string_operand_on_absent_item.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit abd0811).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.update-item-delete-string-operand-on-absent-item</summary>
[Category("AwsEvaluationSuite")]
public sealed class UpdateItemDeleteStringOperandOnAbsentItem
{
    private const string Check = "update-item-delete-string-operand-on-absent-item";

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
    /// UpdateItem's DELETE action with a String operand is refused for its operand type on an
    /// item that was never put, and the refused call creates no item; a Set operand on the same
    /// absent item is accepted as a no-op and likewise creates nothing.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.update-item-delete-string-operand-on-absent-item")]
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

        var key = Item(("pk", S("ghost")));

        await ExpectError(
            async () => await ddb.UpdateItemAsync(new UpdateItemRequest
            {
                TableName = table,
                Key = key,
                UpdateExpression = "DELETE tags :v",
                ExpressionAttributeValues = Item((":v", S("z"))),
            }),
            DeleteString);
        var after = (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = key, ConsistentRead = true })).Item;
        Ensure(after is null, $"the rejected DELETE must not create the item, got {Py.Repr(after)}");

        // Positive control: a Set operand against the same absent item is a documented no-op.
        await ddb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = table,
            Key = key,
            UpdateExpression = "DELETE tags :v",
            ExpressionAttributeValues = Item((":v", SS("z"))),
        });
        var stillAbsent = (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = key, ConsistentRead = true })).Item;
        Ensure(stillAbsent is null, $"the accepted no-op DELETE must not create the item either, got {Py.Repr(stillAbsent)}");
    }
}
