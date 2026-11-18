// Translated from behavioral/dynamodb/check_absence_condition_holds_with_no_previous_item.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.absence-condition-holds-with-no-previous-item</summary>
[Category("AwsEvaluationSuite")]
public sealed class AbsenceConditionHoldsWithNoPreviousItem
{
    private const string CheckId = "absence-condition-holds-with-no-previous-item";
    private static readonly (string Code, string Message) Ccfe = ("ConditionalCheckFailedException", "The conditional request failed");

    /// <summary>
    /// With no item at a key — never written, or most recently deleted — attribute_not_exists(pk)
    /// holds for DeleteItem and PutItem, while attribute_exists(pk) fails them both with
    /// ConditionalCheckFailedException and leaves no item behind.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.absence-condition-holds-with-no-previous-item")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-ddbabs-");
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

        // A key that was never written: a presence condition fails DeleteItem.
        const string neverWritten = "never-written";
        await ExpectError(
            () => ddb.DeleteItemAsync(new DeleteItemRequest
            {
                TableName = table,
                Key = Item(("pk", S(neverWritten))),
                ConditionExpression = "attribute_exists(pk)",
            }),
            Ccfe);
        Ensure(
            await GetItem(ddb, table, neverWritten) is null,
            $"a rejected DeleteItem must not create {Py.Repr(neverWritten)}, got {Py.Repr(await GetItem(ddb, table, neverWritten))}");

        // The identical key's absence condition holds: DeleteItem succeeds with no effect.
        var deletedResponse = await ddb.DeleteItemAsync(new DeleteItemRequest
        {
            TableName = table,
            Key = Item(("pk", S(neverWritten))),
            ConditionExpression = "attribute_not_exists(pk)",
        });
        Ensure(
            deletedResponse.Attributes is null,
            $"DeleteItem on a never-written key returned Attributes: {Py.Repr(deletedResponse.Attributes)}");
        Ensure(
            await GetItem(ddb, table, neverWritten) is null,
            $"expected {Py.Repr(neverWritten)} to remain absent, got {Py.Repr(await GetItem(ddb, table, neverWritten))}");

        // A key that was written, then most recently deleted: the same two conditions apply.
        const string deletedKey = "most-recently-deleted";
        await ddb.PutItemAsync(table, Item(("pk", S(deletedKey)), ("marker", S("seed"))));
        var present = await GetItem(ddb, table, deletedKey);
        Ensure(
            Py.Equal(present, Item(("pk", S(deletedKey)), ("marker", S("seed")))),
            $"expected the seeded item before delete, got {Py.Repr(present)}");

        await ddb.DeleteItemAsync(table, Item(("pk", S(deletedKey))));
        Ensure(
            await GetItem(ddb, table, deletedKey) is null,
            $"expected {Py.Repr(deletedKey)} absent right after its unconditional delete, got {Py.Repr(await GetItem(ddb, table, deletedKey))}");

        // A presence condition fails PutItem against the now-deleted key.
        await ExpectError(
            () => ddb.PutItemAsync(new PutItemRequest
            {
                TableName = table,
                Item = Item(("pk", S(deletedKey)), ("marker", S("should-not-land"))),
                ConditionExpression = "attribute_exists(pk)",
            }),
            Ccfe);
        Ensure(
            await GetItem(ddb, table, deletedKey) is null,
            $"a rejected PutItem must not resurrect {Py.Repr(deletedKey)}, got {Py.Repr(await GetItem(ddb, table, deletedKey))}");

        // The absence condition holds for PutItem against the deleted key, and the write lands.
        await ddb.PutItemAsync(new PutItemRequest
        {
            TableName = table,
            Item = Item(("pk", S(deletedKey)), ("marker", S("restored"))),
            ConditionExpression = "attribute_not_exists(pk)",
        });
        var restored = await GetItem(ddb, table, deletedKey);
        Ensure(
            Py.Equal(restored, Item(("pk", S(deletedKey)), ("marker", S("restored")))),
            $"expected the restored PutItem to land, got {Py.Repr(restored)}");
    }

    /// <summary>(code, message) of the error <paramref name="call"/> raises; fails if it succeeds.</summary>
    private static async Task ExpectError(Func<Task> call, (string Code, string Message) expected)
    {
        var (response, error) = await FidelityContext.Capture(async () =>
        {
            await call();
            return "succeeded";
        });
        if (error is null)
        {
            throw Fail($"expected the call to be rejected, but it succeeded: {response}");
        }

        Ensure(error.Code == expected.Code, $"expected {Py.Repr(expected.Code)}, got {Py.Repr(error.Code)} ({Py.Repr(error.Message)})");
        Ensure(error.Message == expected.Message, $"unexpected message {Py.Repr(error.Message)}");
    }

    private static async Task<Dictionary<string, AttributeValue>?> GetItem(IAmazonDynamoDB ddb, string table, string pk) =>
        (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Item(("pk", S(pk))), ConsistentRead = true })).Item;
}
