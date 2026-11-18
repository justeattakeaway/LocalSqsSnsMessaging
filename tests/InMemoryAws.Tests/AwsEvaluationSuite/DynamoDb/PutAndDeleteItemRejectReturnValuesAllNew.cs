// Translated from behavioral/dynamodb/check_put_and_delete_item_reject_return_values_all_new.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.put-and-delete-item-reject-return-values-all-new</summary>
[Category("AwsEvaluationSuite")]
public sealed class PutAndDeleteItemRejectReturnValuesAllNew
{
    private const string CheckId = "put-and-delete-item-reject-return-values-all-new";
    private const string ErrValidation = "ValidationException";

    // The ReturnValue enum is shared by four operations, but PutItem and DeleteItem accept only two of
    // its members. The other three are individually valid enum values and wrong for these operations.
    private static readonly string[] Refused = ["ALL_NEW", "UPDATED_OLD", "UPDATED_NEW"];

    /// <summary>The (code, message) <paramref name="fn"/> raises; raises AssertionError if the call succeeds.</summary>
    private static async Task<(string? Code, string? Message)> Code<T>(Func<Task<T>> fn)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is not null)
        {
            return (error.Code, error.Message);
        }

        throw Fail($"real AWS refuses this call, but it SUCCEEDED and returned {Py.Repr(result)}");
    }

    /// <summary>
    /// PutItem and DeleteItem refuse ReturnValues ALL_NEW, UPDATED_OLD and UPDATED_NEW with
    /// ValidationException and leave the item untouched, while UpdateItem accepts ALL_NEW.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.put-and-delete-item-reject-return-values-all-new")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-retvals-");
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

        var original = Item(("pk", S("item")), ("body", S("original")));
        await ddb.PutItemAsync(table, original);

        async Task<Dictionary<string, AttributeValue>?> Current() =>
            (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Item(("pk", S("item"))), ConsistentRead = true })).Item;

        // --- PutItem refuses the three members it does not accept ---
        foreach (var value in Refused)
        {
            var (code, message) = await Code(() => ddb.PutItemAsync(new PutItemRequest
            {
                TableName = table,
                Item = Item(("pk", S("item")), ("body", S("overwritten"))),
                ReturnValues = value,
            }));
            Ensure(
                code == ErrValidation,
                $"PutItem accepts only NONE and ALL_OLD for ReturnValues, so {Py.Repr(value)} must be refused with {ErrValidation}; got {code}");
            Ensure(
                message == "1 validation error detected: ReturnValues can only be ALL_OLD or NONE",
                $"expected AWS's exact ValidationException message, got {Py.Repr(message)}");
            Ensure(
                Py.Equal(await Current(), original),
                $"a PutItem refused for ReturnValues={Py.Repr(value)} must not have written its item: expected {Py.Repr(original)} still stored, found {Py.Repr(await Current())}");
        }

        // --- DeleteItem refuses the same three, and deletes nothing when it does ---
        foreach (var value in Refused)
        {
            var (code, message) = await Code(() => ddb.DeleteItemAsync(new DeleteItemRequest
            {
                TableName = table,
                Key = Item(("pk", S("item"))),
                ReturnValues = value,
            }));
            Ensure(
                code == ErrValidation,
                $"DeleteItem accepts only NONE and ALL_OLD for ReturnValues, so {Py.Repr(value)} must be refused with {ErrValidation}; got {code}");
            Ensure(
                message == "1 validation error detected: ReturnValues can only be ALL_OLD or NONE",
                $"expected AWS's exact ValidationException message, got {Py.Repr(message)}");
            Ensure(
                Py.Equal(await Current(), original),
                $"a DeleteItem refused for ReturnValues={Py.Repr(value)} must not have removed the item: expected {Py.Repr(original)} still stored, found {Py.Repr(await Current())}");
        }

        // --- control: UpdateItem accepts what PutItem refuses ---
        var updated = await ddb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = table,
            Key = Item(("pk", S("item"))),
            UpdateExpression = "SET body = :b",
            ExpressionAttributeValues = Item((":b", S("updated"))),
            ReturnValues = ReturnValue.ALL_NEW,
        });
        Ensure(
            Py.Equal(updated.Attributes, Item(("pk", S("item")), ("body", S("updated")))),
            $"UpdateItem does accept ALL_NEW and must return the whole item as it now stands, got {Py.Repr(updated.Attributes)}");

        // --- control: the values PutItem and DeleteItem DO accept still work ---
        var replaced = await ddb.PutItemAsync(new PutItemRequest
        {
            TableName = table,
            Item = Item(("pk", S("item")), ("body", S("replaced"))),
            ReturnValues = ReturnValue.ALL_OLD,
        });
        Ensure(
            Py.Equal(replaced.Attributes, Item(("pk", S("item")), ("body", S("updated")))),
            $"PutItem with ReturnValues=ALL_OLD must return the item as it was before the write, got {Py.Repr(replaced.Attributes)}");
        var removed = await ddb.DeleteItemAsync(new DeleteItemRequest
        {
            TableName = table,
            Key = Item(("pk", S("item"))),
            ReturnValues = ReturnValue.ALL_OLD,
        });
        Ensure(
            Py.Equal(removed.Attributes, Item(("pk", S("item")), ("body", S("replaced")))),
            $"DeleteItem with ReturnValues=ALL_OLD must return the item it removed, got {Py.Repr(removed.Attributes)}");
        Ensure(await Current() is null, $"the item must be gone after the accepted delete, found {Py.Repr(await Current())}");
    }
}
