// Translated from behavioral/dynamodb/check_transact_write_four_simultaneous_failures_all_old_items_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit abd0811).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.transact-write-four-simultaneous-failures-all-old-items-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class TransactWriteFourSimultaneousFailuresAllOldItemsRejected
{
    private const string Check = "transact-write-four-simultaneous-failures-all-old-items-rejected";
    private const string ErrCanceled = "TransactionCanceledException";
    private const string CondFailedMessage = "The conditional request failed";
    private const string CanceledPrefix = "Transaction cancelled, please refer cancellation reasons for specific reasons ";

    /// <summary>
    /// A TransactWriteItems with one failing ConditionCheck, Put, Update and Delete reports four
    /// ConditionalCheckFailed reasons in request order; ALL_OLD on each preserves maps, lists and
    /// sets in full, and ALL_OLD on a Delete whose target never existed carries no Item.
    ///
    /// Reference: https://docs.aws.amazon.com/amazondynamodb/latest/APIReference/API_TransactWriteItems.html
    /// </summary>
    [Test]
    [DisplayName("dynamodb.transact-write-four-simultaneous-failures-all-old-items-rejected")]
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

        var ccItem = Item(
            ("pk", S("cc-target")),
            ("status", S("LOCKED")),
            ("attrs", M(("owner", S("ops")), ("retries", N("3")))));
        var putItem = Item(("pk", S("put-target")), ("tags", L(S("a"), S("b"), N("7"))));
        var updateItem = Item(("pk", S("update-target")), ("version", N("41")), ("owners", SS("alice", "bob")));
        var deleteKey = Item(("pk", S("delete-target-never-created")));
        await ddb.PutItemAsync(table, ccItem);
        await ddb.PutItemAsync(table, putItem);
        await ddb.PutItemAsync(table, updateItem);

        Task<TransactWriteItemsResponse> Attempt(string ccExpected, string putCondition, int updateExpectedVersion, bool deleteKeyPresent) =>
            ddb.TransactWriteItemsAsync(new TransactWriteItemsRequest
            {
                TransactItems =
                [
                    new TransactWriteItem
                    {
                        ConditionCheck = new ConditionCheck
                        {
                            TableName = table,
                            Key = Item(("pk", ccItem["pk"])),
                            ConditionExpression = "#s = :expected",
                            ExpressionAttributeNames = Names(("#s", "status")),
                            ExpressionAttributeValues = Item((":expected", S(ccExpected))),
                            ReturnValuesOnConditionCheckFailure = ReturnValuesOnConditionCheckFailure.ALL_OLD,
                        },
                    },
                    new TransactWriteItem
                    {
                        Put = new Put
                        {
                            TableName = table,
                            Item = Item(("pk", putItem["pk"]), ("tags", L(S("replaced")))),
                            ConditionExpression = putCondition,
                            ReturnValuesOnConditionCheckFailure = ReturnValuesOnConditionCheckFailure.ALL_OLD,
                        },
                    },
                    new TransactWriteItem
                    {
                        Update = new Update
                        {
                            TableName = table,
                            Key = Item(("pk", updateItem["pk"])),
                            UpdateExpression = "SET version = version + :one",
                            ConditionExpression = "version = :v",
                            ExpressionAttributeValues = Item(
                                (":one", N("1")),
                                (":v", N(updateExpectedVersion.ToString(System.Globalization.CultureInfo.InvariantCulture)))),
                            ReturnValuesOnConditionCheckFailure = ReturnValuesOnConditionCheckFailure.ALL_OLD,
                        },
                    },
                    new TransactWriteItem
                    {
                        Delete = new Delete
                        {
                            TableName = table,
                            Key = deleteKey,
                            ConditionExpression = deleteKeyPresent ? "attribute_exists(pk)" : "attribute_not_exists(pk)",
                            ReturnValuesOnConditionCheckFailure = ReturnValuesOnConditionCheckFailure.ALL_OLD,
                        },
                    },
                ],
            });

        // All four fail simultaneously: the ConditionCheck expects the wrong status, the Put's
        // attribute_not_exists is false because put-target already exists, the Update's version
        // guard is stale, and the Delete's attribute_exists is false because its target never
        // existed.
        var err = await FidelityContext.CaptureError(() => Attempt(
            ccExpected: "UNLOCKED", putCondition: "attribute_not_exists(pk)", updateExpectedVersion: 99, deleteKeyPresent: true))
            ?? throw Fail("expected the transaction to be canceled, but it succeeded");
        Ensure(err.Code == ErrCanceled, $"expected {ErrCanceled}, got {Py.Repr(err.Code)}");
        var expectedMessage = CanceledPrefix
            + "[ConditionalCheckFailed, ConditionalCheckFailed, ConditionalCheckFailed, ConditionalCheckFailed]";
        Ensure(err.Message == expectedMessage, $"expected message {Py.Repr(expectedMessage)}, got {Py.Repr(err.Message)}");
        var reasons = err.CancellationReasons();
        Ensure(
            reasons is not null && reasons.Count == 4,
            $"CancellationReasons must be positionally aligned with the 4 TransactItems, got {Py.Repr(reasons?.Select(r => r.Code))}");
        var (ccReason, putReason, updateReason, deleteReason) = (reasons![0], reasons[1], reasons[2], reasons[3]);

        Ensure(ccReason.Code == "ConditionalCheckFailed", $"member 0 (ConditionCheck) must report ConditionalCheckFailed, got {Py.Repr(ccReason.Code)}");
        Ensure(ccReason.Message == CondFailedMessage, Py.Repr(ccReason.Message));
        Ensure(
            Py.Equal(ccReason.Item, ccItem),
            $"ALL_OLD on the ConditionCheck must return the complete item including its map, got {Py.Repr(ccReason.Item)}");

        Ensure(putReason.Code == "ConditionalCheckFailed", $"member 1 (Put) must report ConditionalCheckFailed, got {Py.Repr(putReason.Code)}");
        Ensure(putReason.Message == CondFailedMessage, Py.Repr(putReason.Message));
        Ensure(
            Py.Equal(putReason.Item, putItem),
            $"ALL_OLD on the Put must return the complete conflicting item including its list, got {Py.Repr(putReason.Item)}");

        Ensure(updateReason.Code == "ConditionalCheckFailed", $"member 2 (Update) must report ConditionalCheckFailed, got {Py.Repr(updateReason.Code)}");
        Ensure(updateReason.Message == CondFailedMessage, Py.Repr(updateReason.Message));
        Ensure(
            Py.Equal(updateReason.Item, updateItem),
            $"ALL_OLD on the Update must return the complete item including its string set, got {Py.Repr(updateReason.Item)}");

        Ensure(deleteReason.Code == "ConditionalCheckFailed", $"member 3 (Delete) must report ConditionalCheckFailed, got {Py.Repr(deleteReason.Code)}");
        Ensure(deleteReason.Message == CondFailedMessage, Py.Repr(deleteReason.Message));
        Ensure(
            deleteReason.Item is null,
            $"ALL_OLD on a Delete whose target never existed must carry no Item, got {Py.Repr(deleteReason.Item)}");

        // Effect absent: every item is byte-for-byte unchanged, and the never-created target stays absent.
        Ensure(Py.Equal(await Get(ddb, table, Item(("pk", ccItem["pk"]))), ccItem), "the cc-target item changed");
        Ensure(Py.Equal(await Get(ddb, table, Item(("pk", putItem["pk"]))), putItem), "the put-target item changed");
        Ensure(Py.Equal(await Get(ddb, table, Item(("pk", updateItem["pk"]))), updateItem), "the update-target item changed");
        Ensure(await Get(ddb, table, deleteKey) is null, "the canceled transaction must not have created the delete target");

        // Positive control: the same four action kinds, all satisfiable, commit together.
        await Attempt(ccExpected: "LOCKED", putCondition: "attribute_exists(pk)", updateExpectedVersion: 41, deleteKeyPresent: false);
        Ensure(
            Py.Equal(await Get(ddb, table, Item(("pk", putItem["pk"]))), Item(("pk", putItem["pk"]), ("tags", L(S("replaced"))))),
            "a committed transaction must replace the put-target item");
        var updated = await Get(ddb, table, Item(("pk", updateItem["pk"])));
        Ensure(
            Py.Equal(updated, Item(("pk", updateItem["pk"]), ("version", N("42")), ("owners", updateItem["owners"]))),
            $"a committed transaction must bump the version, got {Py.Repr(updated)}");
        var deleted = await Get(ddb, table, deleteKey);
        Ensure(deleted is null, $"a committed Delete on a still-absent target leaves it absent, got {Py.Repr(deleted)}");
    }

    private static async Task<Dictionary<string, AttributeValue>?> Get(IAmazonDynamoDB ddb, string table, Dictionary<string, AttributeValue> key) =>
        (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = key, ConsistentRead = true })).Item;
}
