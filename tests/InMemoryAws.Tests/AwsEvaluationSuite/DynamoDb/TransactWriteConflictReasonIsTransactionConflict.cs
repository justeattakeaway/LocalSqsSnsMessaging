// Translated from behavioral/dynamodb/check_transact_write_conflict_reason_is_transaction_conflict.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Globalization;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.transact-write-conflict-reason-is-transaction-conflict</summary>
[Category("AwsEvaluationSuite")]
public sealed class TransactWriteConflictReasonIsTransactionConflict
{
    private const string CheckId = "transact-write-conflict-reason-is-transaction-conflict";
    private const string ErrCancelled = "TransactionCanceledException";
    private const string CancelledMessage = "Transaction cancelled, please refer cancellation reasons for specific reasons "
        + "[TransactionConflict, None]";

    private const string ConflictMessage = "Transaction is ongoing for the item";
    private const int Workers = 12;

    private static string Str(int value) => value.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// One unconditional two-item TransactWriteItems: a key every worker shares, and a key this
    /// worker alone touches. Returns null on commit, (message, CancellationReasons) on a cancellation,
    /// and raises anything else. The second key is per-worker so only slot 0 is ever contended.
    /// </summary>
    private static async Task<(string? Message, List<CancellationReason>? Reasons)?> Contend(IAmazonDynamoDB ddb, string table, int worker, int roundNo)
    {
        var error = await FidelityContext.CaptureError(() => ddb.TransactWriteItemsAsync(new TransactWriteItemsRequest
        {
            TransactItems =
            [
                new TransactWriteItem
                {
                    Update = new Update
                    {
                        TableName = table,
                        Key = Item(("pk", S("HOT"))),
                        UpdateExpression = "ADD hits :one",
                        ExpressionAttributeValues = Item((":one", N("1"))),
                    },
                },
                new TransactWriteItem
                {
                    Update = new Update
                    {
                        TableName = table,
                        Key = Item(("pk", S($"OWN#{worker}"))),
                        UpdateExpression = "SET round_no = :r",
                        ExpressionAttributeValues = Item((":r", N(Str(roundNo)))),
                    },
                },
            ],
        }));
        if (error is null)
        {
            return null;
        }

        if (error.Code != ErrCancelled)
        {
            throw error.Exception;
        }

        return (error.Message, error.CancellationReasons());
    }

    /// <summary>
    /// One round of overlapping unconditional TransactWriteItems on a shared item may cancel some
    /// (zero is allowed); every cancellation carries the exact message, a positional reasons list with
    /// TransactionConflict on the shared slot only, and the shared counter equals the commits.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.transact-write-conflict-reason-is-transaction-conflict")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-txconflict-");
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

        await ddb.PutItemAsync(table, Item(("pk", S("HOT")), ("hits", N("0"))));
        for (var worker = 0; worker < Workers; worker++)
        {
            await ddb.PutItemAsync(table, Item(("pk", S($"OWN#{worker}")), ("round_no", N("0"))));
        }

        (string? Message, List<CancellationReason>? Reasons)?[] outcomes;
        using (ctx.Concurrently())
        {
            outcomes = await Task.WhenAll(Enumerable.Range(0, Workers).Select(w => Task.Run(() => Contend(ddb, table, w, 1), cancellationToken)));
        }

        var committed = outcomes.Count(o => o is null);
        var cancellations = outcomes.Where(o => o is not null).Select(o => o!.Value).ToList();

        foreach (var (message, reasonList) in cancellations)
        {
            var reasons = reasonList?.Select(Reason).ToList();
            Ensure(
                message == CancelledMessage,
                $"expected the cancellation message {Py.Repr(CancelledMessage)}, got {Py.Repr(message)}");
            Ensure(
                reasons is not null && reasons.Count == 2,
                "CancellationReasons must carry one entry per TransactItem, in request order: "
                + $"expected 2 entries for a 2-item transaction, got {Py.Repr(reasons)}");
            Ensure(
                reasons[0].GetValueOrDefault("Code") as string == "TransactionConflict",
                "the contended item was refused with no ConditionExpression in the request, so the "
                + "only reason real AWS gives is TransactionConflict; got "
                + $"{Py.Repr(reasons[0].GetValueOrDefault("Code"))} in {Py.Repr(reasons)}");
            Ensure(
                reasons[0].GetValueOrDefault("Message") as string == ConflictMessage,
                $"the contended item must carry {Py.Repr(ConflictMessage)}, got {Py.Repr(reasons[0].GetValueOrDefault("Message"))}");
            Ensure(
                reasons[1].GetValueOrDefault("Code") is null or "None",
                "the second item in this request is touched by no other worker, so it was not the "
                + $"cause and its slot must carry no reason code: got {Py.Repr(reasons[1])}. A code here means "
                + "the reasons list is not positional");
        }

        var hot = (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Item(("pk", S("HOT"))), ConsistentRead = true })).Item;
        Ensure(
            hot["hits"].N == Str(committed),
            "the counter must record exactly the committed transactions and nothing from the "
            + $"cancelled ones: got {Py.Repr(hot["hits"].N)}, expected {committed} after "
            + $"{cancellations.Count} cancellations");
    }

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
