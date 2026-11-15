// Translated from behavioral/dynamodb/check_delete_set_operand_type_mismatch_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Ddb;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.delete-set-operand-type-mismatch-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class DeleteSetOperandTypeMismatchRejected
{
    private const string Check = "delete-set-operand-type-mismatch-rejected";

    /// <summary>(code, message) of the ClientError `fn()` raises; RAISES if the call unexpectedly succeeds.</summary>
    private static async Task<(string? Code, string? Message)> Error(Func<Task<object>> fn)
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is null)
        {
            throw Fail($"real AWS rejects this call, but it SUCCEEDED and returned {result}");
        }

        return (error.Code, error.Message);
    }

    /// <summary>
    /// UpdateItem's DELETE action is refused for a non-set operand, and separately for a set
    /// operand of a type other than the attribute's; a same-typed DELETE still removes an element.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.delete-set-operand-type-mismatch-rejected")]
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
        await ddb.PutItemAsync(table, Item(("pk", S("x")), ("tags", SS("a", "b"))));

        Task<UpdateItemResponse> DeleteFromSet(AttributeValue operand) => ddb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = table,
            Key = Item(("pk", S("x"))),
            UpdateExpression = "DELETE #s :v",
            ExpressionAttributeNames = Names(("#s", "tags")),
            ExpressionAttributeValues = Item((":v", operand)),
        });

        var (code1, message1) = await Error(async () => await DeleteFromSet(N("5")));
        const string expected1 =
            "1 validation error detected: Invalid UpdateExpression: Incorrect operand type for " +
            "operator or function; operator: DELETE, operand type: NUMBER, typeSet: ALLOWED_FOR_ADD_OPERAND";
        Ensure(
            (code1, message1) == ("ValidationException", expected1),
            $"DELETE #s :v with a Number operand raised {Py.Repr((code1, message1))}, expected {Py.Repr(("ValidationException", expected1))}");

        var (code2, message2) = await Error(async () => await DeleteFromSet(NS("1", "2")));
        const string expected2 = "An operand in the update expression has an incorrect data type";
        Ensure(
            (code2, message2) == ("ValidationException", expected2),
            $"DELETE #s :v with a Number Set operand onto a String Set attribute raised {Py.Repr((code2, message2))}, expected {Py.Repr(("ValidationException", expected2))}");

        var unchanged = (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Item(("pk", S("x"))), ConsistentRead = true })).Item;
        Ensure(
            unchanged["tags"].SS.ToHashSet().SetEquals(["a", "b"]),
            $"both rejected DELETEs must leave the attribute untouched, got {Py.Repr(unchanged["tags"])}");

        await DeleteFromSet(SS("a"));
        var shrunk = (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Item(("pk", S("x"))), ConsistentRead = true })).Item;
        Ensure(
            shrunk["tags"].SS.ToHashSet().SetEquals(["b"]),
            $"the positive-control same-typed DELETE must remove the named element, got {Py.Repr(shrunk["tags"])}");
    }
}
