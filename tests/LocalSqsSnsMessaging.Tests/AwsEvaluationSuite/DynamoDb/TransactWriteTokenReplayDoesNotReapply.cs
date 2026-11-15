// Translated from behavioral/dynamodb/check_transact_write_token_replay_does_not_reapply.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Ddb;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.transact-write-token-replay-does-not-reapply</summary>
[Category("AwsEvaluationSuite")]
public sealed class TransactWriteTokenReplayDoesNotReapply
{
    private const string ErrTransactionCanceled = "TransactionCanceledException";

    /// <summary>
    /// A TransactWriteItems replay under the same ClientRequestToken returns the first outcome without re-executing:
    /// an ADD counter stays at 1 and a now-failing condition still succeeds; a fresh token re-applies the ADD and
    /// raises TransactionCanceledException with the exact message for the failing condition.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.transact-write-token-replay-does-not-reapply")]
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
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(t));
        await ddb.WaitForTableExists(t, delaySeconds: 2, maxAttempts: 250);

        async Task<string> Counter()
        {
            var item = (await ddb.GetItemAsync(new GetItemRequest { TableName = t, Key = Item(("pk", S("ctr"))), ConsistentRead = true })).Item;
            return item is null ? "None" : item["n"].N;
        }

        List<TransactWriteItem> Increment() =>
        [
            new TransactWriteItem
            {
                Update = new Update
                {
                    TableName = t,
                    Key = Item(("pk", S("ctr"))),
                    UpdateExpression = "ADD n :one",
                    ExpressionAttributeValues = Item((":one", N("1"))),
                },
            },
        ];

        // --- a non-idempotent ADD proves the replay was NOT re-executed ---
        var token = Guid.NewGuid().ToString();
        await ddb.TransactWriteItemsAsync(new TransactWriteItemsRequest { TransactItems = Increment(), ClientRequestToken = token });
        Ensure(await Counter() == "1", $"first call must set the counter to 1, got {await Counter()}");

        for (var attempt = 0; attempt < 3; attempt++)
        {
            await ddb.TransactWriteItemsAsync(new TransactWriteItemsRequest { TransactItems = Increment(), ClientRequestToken = token });
            Ensure(
                await Counter() == "1",
                $"replay {attempt + 1} with the SAME ClientRequestToken must be deduplicated, not re-executed; counter is {await Counter()} (expected 1)");
        }

        // --- a DIFFERENT token is a different transaction and does apply ---
        await ddb.TransactWriteItemsAsync(new TransactWriteItemsRequest { TransactItems = Increment(), ClientRequestToken = Guid.NewGuid().ToString() });
        Ensure(await Counter() == "2", $"a fresh token must apply the ADD again, got {await Counter()}");

        // --- replaying a create-once transaction must return the cached SUCCESS, not the
        //     ConditionalCheckFailed that a re-execution would now produce ---
        var createOnceToken = Guid.NewGuid().ToString();
        List<TransactWriteItem> CreateOnce() =>
        [
            new TransactWriteItem
            {
                Put = new Put
                {
                    TableName = t,
                    Item = Item(("pk", S("once")), ("payload", S("first"))),
                    ConditionExpression = "attribute_not_exists(pk)",
                },
            },
        ];
        await ddb.TransactWriteItemsAsync(new TransactWriteItemsRequest { TransactItems = CreateOnce(), ClientRequestToken = createOnceToken });
        var created = (await ddb.GetItemAsync(new GetItemRequest { TableName = t, Key = Item(("pk", S("once"))), ConsistentRead = true })).Item;
        Ensure(Py.Equal(created, Item(("pk", S("once")), ("payload", S("first")))), Py.Repr(created));

        // This is the assertion an SDK retry depends on: it must NOT raise.
        await ddb.TransactWriteItemsAsync(new TransactWriteItemsRequest { TransactItems = CreateOnce(), ClientRequestToken = createOnceToken });
        var unchanged = (await ddb.GetItemAsync(new GetItemRequest { TableName = t, Key = Item(("pk", S("once"))), ConsistentRead = true })).Item;
        Ensure(
            Py.Equal(unchanged, Item(("pk", S("once")), ("payload", S("first")))),
            $"the deduplicated replay must leave the item untouched, got {Py.Repr(unchanged)}");

        // And a fresh token for the same create-once transaction DOES hit the condition, proving the
        // condition itself is still being evaluated (the dedup is token-scoped, not condition-blind).
        var error = await FidelityContext.CaptureError(() => ddb.TransactWriteItemsAsync(new TransactWriteItemsRequest
        {
            TransactItems = CreateOnce(),
            ClientRequestToken = Guid.NewGuid().ToString(),
        }));
        var code = error?.Code;
        var message = error?.Message;
        Ensure(
            code == ErrTransactionCanceled,
            $"with a NEW token the attribute_not_exists condition must fail, proving dedup is token-scoped; got {code ?? "None"}");
        Ensure(
            message == "Transaction cancelled, please refer cancellation reasons for specific reasons [ConditionalCheckFailed]",
            $"expected AWS's exact TransactionCanceledException message, got {Py.Repr(message)}");
    }
}
