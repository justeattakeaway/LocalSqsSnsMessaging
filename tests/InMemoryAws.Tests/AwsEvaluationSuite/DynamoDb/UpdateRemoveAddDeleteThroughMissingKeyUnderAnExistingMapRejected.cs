// Translated from behavioral/dynamodb/check_update_remove_add_delete_through_missing_key_under_an_existing_map_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit abd0811).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.update-remove-add-delete-through-missing-key-under-an-existing-map-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class UpdateRemoveAddDeleteThroughMissingKeyUnderAnExistingMapRejected
{
    private const string Check = "update-remove-add-delete-through-missing-key-under-an-existing-map-rejected";

    private static readonly (string Code, string Message) PathInvalid = (
        "ValidationException",
        "The document path provided in the update expression is invalid for update");

    /// <summary>Call fn() and assert the error it raises carries exactly code and message.</summary>
    private static async Task ExpectError(Func<Task<object>> fn, (string Code, string Message) expected)
    {
        var (result, err) = await FidelityContext.Capture(fn);
        if (err is null)
        {
            throw Fail($"expected {Py.Repr(expected.Code)}, but the call succeeded: {result}");
        }

        Ensure(err.Code == expected.Code, $"expected {Py.Repr(expected.Code)}, got {Py.Repr(err.Code)}: {Py.Repr(err.Message)}");
        Ensure(err.Message == expected.Message, $"expected message {Py.Repr(expected.Message)}, got {Py.Repr(err.Message)}");
    }

    /// <summary>
    /// REMOVE/ADD/DELETE through a key missing from an existing map reject with the invalid
    /// document path error and leave the item untouched; REMOVE of that same missing key alone
    /// (no further traversal) is a no-op, and a real leaf one level deeper still comes off.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.update-remove-add-delete-through-missing-key-under-an-existing-map-rejected")]
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

        var key = Item(("pk", S("doc")));
        var original = Item(
            ("pk", S("doc")),
            ("keep", S("k")),
            ("m", M(("a", M(("b", S("x")))))));
        await ddb.PutItemAsync(table, original);

        async Task<Dictionary<string, AttributeValue>> Read() =>
            (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = key, ConsistentRead = true })).Item;

        // The parent of "child" ("m.absent") does not exist, even though "m" itself does.
        await ExpectError(
            async () => await ddb.UpdateItemAsync(new UpdateItemRequest { TableName = table, Key = key, UpdateExpression = "REMOVE m.absent.child" }),
            PathInvalid);
        await ExpectError(
            async () => await ddb.UpdateItemAsync(new UpdateItemRequest
            {
                TableName = table,
                Key = key,
                UpdateExpression = "ADD m.absent.n :one",
                ExpressionAttributeValues = Item((":one", N("1"))),
            }),
            PathInvalid);
        await ExpectError(
            async () => await ddb.UpdateItemAsync(new UpdateItemRequest
            {
                TableName = table,
                Key = key,
                UpdateExpression = "DELETE m.absent.s :v",
                ExpressionAttributeValues = Item((":v", SS("x"))),
            }),
            PathInvalid);

        var unchanged = await Read();
        Ensure(
            Py.Equal(unchanged, original),
            $"after the three refused updates the item is {Py.Repr(unchanged)}, expected it unchanged " +
            $"from {Py.Repr(original)}");

        // REMOVE of "m.absent" itself (the leaf, with no further traversal past it) is a documented
        // no-op: the path's only missing component is the final one, so there is nothing to validate
        // past it.
        await ddb.UpdateItemAsync(new UpdateItemRequest { TableName = table, Key = key, UpdateExpression = "REMOVE m.absent" });
        var afterNoop = await Read();
        Ensure(
            Py.Equal(afterNoop, original),
            "REMOVE m.absent (a missing leaf under an existing map) must be a no-op, got " +
            $"{Py.Repr(afterNoop)}, expected {Py.Repr(original)}");

        // Positive control: the real leaf one level deeper than the probed path still removes.
        await ddb.UpdateItemAsync(new UpdateItemRequest { TableName = table, Key = key, UpdateExpression = "REMOVE m.a.b" });
        var afterRemove = await Read();
        Ensure(
            Py.Equal(afterRemove["m"], M(("a", M()))),
            $"REMOVE m.a.b on a real leaf left {Py.Repr(afterRemove["m"])}, expected m.a to be an empty map");
        Ensure(
            Py.Equal(afterRemove["keep"], S("k")),
            $"the sibling attribute must still read its original value: {Py.Repr(afterRemove)}");
    }
}
