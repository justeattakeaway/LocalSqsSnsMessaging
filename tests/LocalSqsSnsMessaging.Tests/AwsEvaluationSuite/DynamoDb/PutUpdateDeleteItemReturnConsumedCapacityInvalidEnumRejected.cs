// Translated from behavioral/dynamodb/check_put_update_delete_item_return_consumed_capacity_invalid_enum_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Ddb;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.put-update-delete-item-return-consumed-capacity-invalid-enum-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class PutUpdateDeleteItemReturnConsumedCapacityInvalidEnumRejected
{
    private static readonly (string Code, string Message) Validation = (
        "ValidationException",
        "1 validation error detected: Value 'BOGUS' at 'returnConsumedCapacity' failed to satisfy "
        + "constraint: Member must satisfy enum value set: [INDEXES, TOTAL, NONE]");

    // UpdateItem answers the same constraint without the "1 validation error detected: " prefix.
    private static readonly (string Code, string Message) ValidationUpdate = (
        "ValidationException",
        "Value 'BOGUS' at 'returnConsumedCapacity' failed to satisfy constraint: Member must satisfy "
        + "enum value set: [INDEXES, TOTAL, NONE]");

    private static readonly ReturnConsumedCapacity Bogus = new("BOGUS");

    /// <summary>(Error.Code, Error.Message) of the ClientError fn() raises; an unexpected success fails.</summary>
    private static async Task<(string? Code, string? Message)> Error(Func<Task<object>> fn)
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is not null)
        {
            return (error.Code, error.Message);
        }

        throw Fail($"expected the call to be rejected, but it succeeded: {Py.Repr(result)}");
    }

    private static async Task ExpectError(Func<Task<object>> fn, (string Code, string Message) expected)
    {
        var got = await Error(fn);
        Ensure(got == expected, $"expected {Py.Repr(expected)}, got {Py.Repr(got)}");
    }

    /// <summary>
    /// PutItem, UpdateItem and DeleteItem each reject an out-of-enum ReturnConsumedCapacity with a
    /// ValidationException naming the offending value, and the item is left untouched.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.put-update-delete-item-return-consumed-capacity-invalid-enum-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-ddbrccenum-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(table));
        await ddb.WaitForTableExists(table, delaySeconds: 2, maxAttempts: 250);

        async Task<Dictionary<string, AttributeValue>?> Get() =>
            (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Item(("pk", S("a"))), ConsistentRead = true })).Item;

        // PutItem: the invalid call is rejected and no item is created.
        await ExpectError(
            async () => await ddb.PutItemAsync(new PutItemRequest
            {
                TableName = table,
                Item = Item(("pk", S("a")), ("v", S("1"))),
                ReturnConsumedCapacity = Bogus,
            }),
            Validation);
        Ensure(await Get() is null, "the rejected PutItem must not have created an item");
        await ddb.PutItemAsync(table, Item(("pk", S("a")), ("v", S("1"))));
        var created = (await Get())!;
        Ensure(created["v"].S == "1", $"the positive-control PutItem did not persist: {Py.Repr(created)}");

        // UpdateItem: the invalid call is rejected and the existing value is left in place.
        await ExpectError(
            async () => await ddb.UpdateItemAsync(new UpdateItemRequest
            {
                TableName = table,
                Key = Item(("pk", S("a"))),
                UpdateExpression = "SET v = :v",
                ExpressionAttributeValues = Item((":v", S("2"))),
                ReturnConsumedCapacity = Bogus,
            }),
            ValidationUpdate);
        var unchanged = (await Get())!;
        Ensure(unchanged["v"].S == "1", $"the rejected UpdateItem must not have changed the item: {Py.Repr(unchanged)}");
        await ddb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = table,
            Key = Item(("pk", S("a"))),
            UpdateExpression = "SET v = :v",
            ExpressionAttributeValues = Item((":v", S("2"))),
        });
        var updated = (await Get())!;
        Ensure(updated["v"].S == "2", $"the positive-control UpdateItem did not persist: {Py.Repr(updated)}");

        // DeleteItem: the invalid call is rejected and the item survives.
        await ExpectError(
            async () => await ddb.DeleteItemAsync(new DeleteItemRequest
            {
                TableName = table,
                Key = Item(("pk", S("a"))),
                ReturnConsumedCapacity = Bogus,
            }),
            Validation);
        var survives = await Get();
        Ensure(survives is not null && survives["v"].S == "2", $"the rejected DeleteItem must not have deleted the item: {Py.Repr(survives)}");
        await ddb.DeleteItemAsync(table, Item(("pk", S("a"))));
        Ensure(await Get() is null, "the positive-control DeleteItem must have removed the item");
    }
}
