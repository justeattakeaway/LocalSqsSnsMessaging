// Translated from behavioral/dynamodb/check_transact_write_delete_condition_failure_cancels_transaction.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Ddb;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.transact-write-delete-condition-failure-cancels-transaction</summary>
[Category("AwsEvaluationSuite")]
public sealed class TransactWriteDeleteConditionFailureCancelsTransaction
{
    private const string ErrCanceled = "TransactionCanceledException";
    private const string CondFailedMsg = "The conditional request failed";
    private const string CanceledPrefix = "Transaction cancelled, please refer cancellation reasons for specific reasons ";

    /// <summary>
    /// A Delete member's failed ConditionExpression cancels the whole TransactWriteItems call;
    /// the targeted item is not deleted and a sibling Put member in the same call does not commit.
    ///
    /// Reference: https://docs.aws.amazon.com/amazondynamodb/latest/APIReference/API_TransactWriteItems.html
    /// </summary>
    [Test]
    [DisplayName("dynamodb.transact-write-delete-condition-failure-cancels-transaction")]
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
            Tags = [new Tag { Key = "sim2real", Value = "transact-write-delete-condition-failure-cancels-transaction" }],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(table));
        await ddb.WaitForTableExists(table, delaySeconds: 2, maxAttempts: 250);

        var target = Item(("pk", S("locked-record")), ("status", S("LOCKED")));
        await ddb.PutItemAsync(table, Item(("pk", S("locked-record")), ("status", S("LOCKED"))));

        // The Delete member's condition requires status = UNLOCKED, which is false; the Put member
        // would otherwise succeed on its own.
        var err = await Canceled(() => ddb.TransactWriteItemsAsync(new TransactWriteItemsRequest
        {
            TransactItems =
            [
                new TransactWriteItem
                {
                    Delete = new Delete
                    {
                        TableName = table,
                        Key = Item(("pk", S("locked-record"))),
                        ConditionExpression = "#s = :expected",
                        ExpressionAttributeNames = Names(("#s", "status")),
                        ExpressionAttributeValues = Item((":expected", S("UNLOCKED"))),
                        ReturnValuesOnConditionCheckFailure = ReturnValuesOnConditionCheckFailure.ALL_OLD,
                    },
                },
                new TransactWriteItem
                {
                    Put = new Put { TableName = table, Item = Item(("pk", S("sibling-record")), ("status", S("CREATED"))) },
                },
            ],
        }));

        Ensure(err.Code == ErrCanceled, $"expected {ErrCanceled}, got {Py.Repr(err.Code)}");
        var expectedMessage = CanceledPrefix + "[ConditionalCheckFailed, None]";
        Ensure(err.Message == expectedMessage, $"expected message {Py.Repr(expectedMessage)}, got {Py.Repr(err.Message)}");

        var reasons = err.CancellationReasons();
        Ensure(
            reasons is not null && reasons.Count == 2,
            $"CancellationReasons must be positionally aligned with the 2 TransactItems, got {Py.Repr(reasons?.Select(Reason).ToList())}");
        var (deleteReason, putReason) = (reasons![0], reasons[1]);
        Ensure(
            deleteReason.Code == "ConditionalCheckFailed",
            $"the Delete member must report ConditionalCheckFailed, got {Py.Repr(Reason(deleteReason))}");
        Ensure(
            deleteReason.Message == CondFailedMsg,
            $"the Delete member must carry {Py.Repr(CondFailedMsg)}, got {Py.Repr(deleteReason.Message)}");
        Ensure(
            Py.Equal(deleteReason.Item, target),
            "ReturnValuesOnConditionCheckFailure=ALL_OLD must return the complete conflicting item "
            + $"{Py.Repr(target)}, got {Py.Repr(deleteReason.Item)}");
        Ensure(putReason.Code == "None", $"the Put member must report Code 'None', got {Py.Repr(Reason(putReason))}");
        Ensure(putReason.Item is null, $"the non-failing member must not carry an Item, got {Py.Repr(putReason.Item)}");

        // Effect absent: the targeted item is still there, unchanged, and the sibling was never created.
        var still = await Get(ddb, table, "locked-record");
        Ensure(Py.Equal(still, target), $"the canceled Delete must leave the item unchanged, got {Py.Repr(still)}");
        Ensure(await Get(ddb, table, "sibling-record") is null, "the canceled transaction must not have created the sibling item");

        // Positive control: the same shape, with a satisfiable condition, deletes the item and
        // commits the sibling Put.
        await ddb.TransactWriteItemsAsync(new TransactWriteItemsRequest
        {
            TransactItems =
            [
                new TransactWriteItem
                {
                    Delete = new Delete
                    {
                        TableName = table,
                        Key = Item(("pk", S("locked-record"))),
                        ConditionExpression = "#s = :expected",
                        ExpressionAttributeNames = Names(("#s", "status")),
                        ExpressionAttributeValues = Item((":expected", S("LOCKED"))),
                    },
                },
                new TransactWriteItem
                {
                    Put = new Put { TableName = table, Item = Item(("pk", S("sibling-record")), ("status", S("CREATED"))) },
                },
            ],
        });
        Ensure(await Get(ddb, table, "locked-record") is null, "a satisfiable condition must let the Delete remove the item");
        var committed = await Get(ddb, table, "sibling-record");
        Ensure(
            Py.Equal(committed, Item(("pk", S("sibling-record")), ("status", S("CREATED")))),
            $"a committed transaction must write the sibling Put, got {Py.Repr(committed)}");
    }

    /// <summary>The error <paramref name="fn"/> raises; fails if it unexpectedly succeeds.</summary>
    private static async Task<AwsError> Canceled(Func<Task> fn) =>
        await FidelityContext.CaptureError(fn)
        ?? throw Fail("expected the transaction to be canceled, but it succeeded");

    private static async Task<Dictionary<string, AttributeValue>?> Get(IAmazonDynamoDB ddb, string table, string pk) =>
        (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Item(("pk", S(pk))), ConsistentRead = true })).Item;

    /// <summary>A cancellation reason as boto3 hands it back.</summary>
    private static Dictionary<string, object?> Reason(CancellationReason reason)
    {
        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (reason.Item is not null)
        {
            result["Item"] = reason.Item;
        }

        if (reason.Code is not null)
        {
            result["Code"] = reason.Code;
        }

        if (reason.Message is not null)
        {
            result["Message"] = reason.Message;
        }

        return result;
    }
}
