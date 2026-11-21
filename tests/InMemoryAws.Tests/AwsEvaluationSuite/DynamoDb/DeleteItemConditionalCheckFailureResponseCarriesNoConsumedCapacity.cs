// Translated from behavioral/dynamodb/check_delete_item_conditional_check_failure_response_carries_no_consumed_capacity.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit abd0811).
using System.Text.Json;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.delete-item-conditional-check-failure-response-carries-no-consumed-capacity</summary>
[Category("AwsEvaluationSuite")]
public sealed class DeleteItemConditionalCheckFailureResponseCarriesNoConsumedCapacity
{
    private const string Check = "delete-item-conditional-check-failure-response-carries-no-consumed-capacity";

    private static readonly (string Code, string Message) Ccfe = ("ConditionalCheckFailedException", "The conditional request failed");

    /// <summary>
    /// An unconditional DeleteItem with ReturnConsumedCapacity=TOTAL reports the write units it
    /// used, while the ConditionalCheckFailedException answer to a DeleteItem whose ConditionExpression
    /// fails carries no ConsumedCapacity member and the item stays exactly as written.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.delete-item-conditional-check-failure-response-carries-no-consumed-capacity")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-delccf-");
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

        // --- Positive control: an unconditional DeleteItem of a real item really deletes and bills.
        await ddb.PutItemAsync(table, Item(("pk", S("control")), ("v", S("ctrl"))));
        var controlCc = (await ddb.DeleteItemAsync(new DeleteItemRequest
        {
            TableName = table,
            Key = Item(("pk", S("control"))),
            ReturnConsumedCapacity = ReturnConsumedCapacity.TOTAL,
        })).ConsumedCapacity;
        Ensure(
            controlCc.CapacityUnits == 1.0,
            "an unconditional DeleteItem of a small item must bill 1.0 write capacity unit, got " +
            $"{Py.Repr(controlCc.CapacityUnits)}");
        Ensure(
            (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Item(("pk", S("control"))), ConsistentRead = true })).Item is null,
            "the control item must be gone after its unconditional delete");

        // --- A failing ConditionExpression rejects the delete and bills nothing.
        // (A raw request: the .NET SDK does not surface an error response's other members.)
        await ddb.PutItemAsync(table, Item(("pk", S("guarded")), ("v", S("untouched"))));
        var response = await ExpectError(
            ctx,
            "DeleteItem",
            JsonSerializer.Serialize(new
            {
                TableName = table,
                Key = new { pk = new { S = "guarded" } },
                ConditionExpression = "attribute_not_exists(pk)",
                ReturnConsumedCapacity = "TOTAL",
            }),
            Ccfe.Code,
            Ccfe.Message);
        Ensure(
            !response.TryGetProperty("ConsumedCapacity", out var consumed),
            "a rejected conditional DeleteItem must carry no ConsumedCapacity member, got " +
            $"{Py.Repr(consumed)}");
        var stillThere = (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Item(("pk", S("guarded"))), ConsistentRead = true })).Item;
        Ensure(
            Py.Equal(stillThere, Item(("pk", S("guarded")), ("v", S("untouched")))),
            $"the rejected conditional DeleteItem must leave the item exactly as written, got {Py.Repr(stillThere)}");
    }

    /// <summary>The error response of a call that must be refused with exactly (code, message).</summary>
    private static async Task<JsonElement> ExpectError(FidelityContext ctx, string operation, string body, string code, string message)
    {
        var raw = await ctx.DynamoDbRaw(operation, body);
        if ((int)raw.StatusCode < 400)
        {
            throw Fail($"expected the call to be rejected, but it succeeded: {raw.Body}");
        }

        Ensure(raw.ErrorCode == code, $"expected {Py.Repr(code)}, got {Py.Repr(raw.ErrorCode)} ({Py.Repr(raw.ErrorMessage)})");
        Ensure(raw.ErrorMessage == message, $"unexpected message {Py.Repr(raw.ErrorMessage)}");
        return raw.Json!.Value;
    }
}
