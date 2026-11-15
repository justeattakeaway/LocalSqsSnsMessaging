// Translated from behavioral/dynamodb/check_transact_write_condition_check_failure_returns_item_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Ddb;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.transact-write-condition-check-failure-returns-item-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class TransactWriteConditionCheckFailureReturnsItemRejected
{
    private const string ErrTransactionCanceled = "TransactionCanceledException";
    private const string ConditionFailedMessage = "The conditional request failed";
    private const string CanceledPrefix = "Transaction cancelled, please refer cancellation reasons for specific reasons ";

    /// <summary>
    /// With ReturnValuesOnConditionCheckFailure=ALL_OLD only the failing member's reason carries the
    /// conflicting Item and a Message; the passing member reports Code='None' with neither, nothing is
    /// written, and without the opt-in no Item is returned.
    ///
    /// Reference: https://docs.aws.amazon.com/amazondynamodb/latest/APIReference/API_TransactWriteItems.html
    /// </summary>
    [Test]
    [DisplayName("dynamodb.transact-write-condition-check-failure-returns-item-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var t = FidelityContext.UniqueName("sim2real-behav-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = t,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real", Value = "transact-write-condition-check-failure" }],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(t));
        await ddb.WaitForTableExists(t, delaySeconds: 2, maxAttempts: 250);

        var existing = Item(("pk", S("A")), ("version", N("1")), ("payload", S("original")));
        await ddb.PutItemAsync(t, Item(("pk", S("A")), ("version", N("1")), ("payload", S("original"))));

        // Member 0 would succeed; member 1's optimistic-lock condition is stale and fails.
        var err = await FidelityContext.CaptureError(() => ddb.TransactWriteItemsAsync(new TransactWriteItemsRequest
        {
            TransactItems =
            [
                new TransactWriteItem { Put = new Put { TableName = t, Item = Item(("pk", S("C")), ("payload", S("never"))) } },
                new TransactWriteItem
                {
                    Put = new Put
                    {
                        TableName = t,
                        Item = Item(("pk", S("A")), ("version", N("2")), ("payload", S("clobbered"))),
                        ConditionExpression = "version = :stale",
                        ExpressionAttributeValues = Item((":stale", N("99"))),
                        ReturnValuesOnConditionCheckFailure = ReturnValuesOnConditionCheckFailure.ALL_OLD,
                    },
                },
            ],
        }));

        Ensure(err is not null, "a stale optimistic-lock condition must cancel the transaction");
        Ensure(err.Code == ErrTransactionCanceled, $"expected {ErrTransactionCanceled}, got {err.Code}");
        var expected = CanceledPrefix + "[None, ConditionalCheckFailed]";
        Ensure(err.Message == expected, $"expected message {Py.Repr(expected)}, got {Py.Repr(err.Message)}");

        var reasons = err.CancellationReasons();
        Ensure(
            reasons is not null && reasons.Count == 2,
            $"CancellationReasons must be positionally aligned with the 2 TransactItems, got {Py.Repr(reasons?.Select(Reason).ToList())}");

        var (ok, failed) = (reasons![0], reasons[1]);
        Ensure(ok.Code == "None", $"the non-failing member must report Code \"None\", got {Py.Repr(Reason(ok))}");
        Ensure(ok.Item is null, $"the non-failing member must NOT carry an Item, got {Py.Repr(ok.Item)}");
        Ensure(ok.Message is null, $"the non-failing member must NOT carry a Message, got {Py.Repr(ok.Message)}");

        Ensure(failed.Code == "ConditionalCheckFailed", $"the failing member must report ConditionalCheckFailed, got {Py.Repr(Reason(failed))}");
        Ensure(
            failed.Message == ConditionFailedMessage,
            $"the failing member must carry {Py.Repr(ConditionFailedMessage)}, got {Py.Repr(failed.Message)}");
        Ensure(
            Py.Equal(failed.Item, existing),
            "ReturnValuesOnConditionCheckFailure=ALL_OLD must return the COMPLETE conflicting item "
            + $"{Py.Repr(existing)}, got {Py.Repr(failed.Item)}");

        // Effect absent: the would-have-succeeded member wrote nothing.
        Ensure(
            await Get(ddb, t, "C") is null,
            "the transaction was canceled, so member 0 must not have created item C");
        // And the conflicting item is byte-for-byte unchanged.
        var still = await Get(ddb, t, "A");
        Ensure(Py.Equal(still, existing), $"item A must be unchanged by the canceled transaction, got {Py.Repr(still)}");

        // Without ReturnValuesOnConditionCheckFailure the reason must carry NO Item -- the caller
        // opts in; the emulator must not volunteer it.
        var optOut = await FidelityContext.CaptureError(() => ddb.TransactWriteItemsAsync(new TransactWriteItemsRequest
        {
            TransactItems =
            [
                new TransactWriteItem
                {
                    Put = new Put
                    {
                        TableName = t,
                        Item = Item(("pk", S("A")), ("version", N("3"))),
                        ConditionExpression = "version = :stale",
                        ExpressionAttributeValues = Item((":stale", N("99"))),
                    },
                },
            ],
        }));
        var optOutRepr = optOut is null ? "None" : optOut.ToString();
        Ensure(optOut is not null && optOut.Code == ErrTransactionCanceled, optOutRepr);
        expected = CanceledPrefix + "[ConditionalCheckFailed]";
        Ensure(optOut.Message == expected, $"expected message {Py.Repr(expected)}, got {Py.Repr(optOut.Message)}");
        var silent = optOut.CancellationReasons()![0];
        Ensure(silent.Code == "ConditionalCheckFailed", Py.Repr(Reason(silent)));
        Ensure(
            silent.Message == ConditionFailedMessage,
            $"the failing member must carry {Py.Repr(ConditionFailedMessage)}, got {Py.Repr(silent.Message)}");
        Ensure(
            silent.Item is null,
            $"without ReturnValuesOnConditionCheckFailure the reason must omit Item, got {Py.Repr(silent.Item)}");

        // Control: the same transaction with a satisfiable condition commits both members.
        await ddb.TransactWriteItemsAsync(new TransactWriteItemsRequest
        {
            TransactItems =
            [
                new TransactWriteItem { Put = new Put { TableName = t, Item = Item(("pk", S("C")), ("payload", S("committed"))) } },
                new TransactWriteItem
                {
                    Put = new Put
                    {
                        TableName = t,
                        Item = Item(("pk", S("A")), ("version", N("2")), ("payload", S("updated"))),
                        ConditionExpression = "version = :cur",
                        ExpressionAttributeValues = Item((":cur", N("1"))),
                        ReturnValuesOnConditionCheckFailure = ReturnValuesOnConditionCheckFailure.ALL_OLD,
                    },
                },
            ],
        });
        var cAfter = await Get(ddb, t, "C");
        Ensure(
            Py.Equal(cAfter, Item(("pk", S("C")), ("payload", S("committed")))),
            $"a transaction with a satisfiable condition must commit member 0, got {Py.Repr(cAfter)}");
        var aAfter = await Get(ddb, t, "A");
        Ensure(
            Py.Equal(aAfter, Item(("pk", S("A")), ("version", N("2")), ("payload", S("updated")))),
            $"a transaction with a satisfiable condition must commit member 1, got {Py.Repr(aAfter)}");
    }

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
