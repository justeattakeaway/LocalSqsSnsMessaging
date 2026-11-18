// Translated from behavioral/dynamodb/check_transact_write_stale_version_update_cancels_its_companion_put.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Globalization;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.transact-write-stale-version-update-cancels-its-companion-put</summary>
[Category("AwsEvaluationSuite")]
public sealed class TransactWriteStaleVersionUpdateCancelsItsCompanionPut
{
    private const string Check = "transact-write-stale-version-update-cancels-its-companion-put";
    private const string ErrCanceled = "TransactionCanceledException";

    /// <summary>Bound on each wait; a target that needs longer fails with the last answer it gave.</summary>
    private const double WaitS = 40.0;

    private static readonly int[] StaleVersions = [40, 42];

    /// <summary>
    /// A TransactWriteItems whose version-guarded Update is stale is cancelled with per-item reasons
    /// [ConditionalCheckFailed, None] and writes neither item; the same transaction with the current
    /// version commits the version bump and the companion Put together.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.transact-write-stale-version-update-cancels-its-companion-put")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            KeySchema = [new KeySchemaElement("id", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("id", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real", Value = Check }],
        });
        ctx.OnCleanup(() => DeleteTableIfExists(ddb, table));
        await WaitTableActive(ddb, table);

        var account = Item(("id", S("account-7")), ("version", N("41")), ("balance", N("100")));
        await ddb.PutItemAsync(table, account);
        var entryKey = Item(("id", S("ledger-7-42")));

        Task<TransactWriteItemsResponse> Transfer(int expectedVersion) =>
            ddb.TransactWriteItemsAsync(new TransactWriteItemsRequest
            {
                TransactItems =
                [
                    new TransactWriteItem
                    {
                        Update = new Update
                        {
                            TableName = table,
                            Key = Item(("id", account["id"])),
                            UpdateExpression = "SET version = version + :one, balance = balance - :amt",
                            ConditionExpression = "version = :v",
                            ExpressionAttributeValues = Item(
                                (":one", N("1")),
                                (":amt", N("30")),
                                (":v", N(expectedVersion.ToString(CultureInfo.InvariantCulture)))),
                        },
                    },
                    new TransactWriteItem
                    {
                        Put = new Put
                        {
                            TableName = table,
                            Item = Item(("id", entryKey["id"]), ("amount", N("30"))),
                            ConditionExpression = "attribute_not_exists(id)",
                        },
                    },
                ],
            });

        foreach (var stale in StaleVersions)
        {
            var exc = await ExpectError(() => Transfer(stale), [ErrCanceled]);
            var message = exc.Message;
            Ensure(
                message == "Transaction cancelled, please refer cancellation reasons for specific "
                    + "reasons [ConditionalCheckFailed, None]",
                $"the transaction expecting version {stale}: got the message {Py.Repr(message)}");
            var reasons = (exc.CancellationReasons() ?? []).Select(r => (r.Code, r.Message)).ToList();
            Ensure(
                reasons.Count == 2
                    && reasons[0] == ("ConditionalCheckFailed", "The conditional request failed")
                    && reasons[1] == ("None", null),
                $"the transaction expecting version {stale}: got the cancellation reasons {Py.Repr(reasons)}");
            await AssertState(ddb, table, account, entryKey, null, $"the cancelled version-{stale} transaction");
        }

        await Transfer(41);
        var committed = Item(("id", account["id"]), ("version", N("42")), ("balance", N("70")));
        await AssertState(ddb, table, committed, entryKey, Item(("id", entryKey["id"]), ("amount", N("30"))), "the committed transaction");

        // Replaying the committed transaction now fails both guards.
        var replayExc = await ExpectError(() => Transfer(41), [ErrCanceled]);
        var replayMessage = replayExc.Message;
        Ensure(
            replayMessage == "Transaction cancelled, please refer cancellation reasons for specific "
                + "reasons [ConditionalCheckFailed, ConditionalCheckFailed]",
            $"the replayed transaction: got the message {Py.Repr(replayMessage)}");
        await AssertState(ddb, table, committed, entryKey, Item(("id", entryKey["id"]), ("amount", N("30"))), "the replayed transaction");
    }

    private static async Task AssertState(
        IAmazonDynamoDB ddb,
        string table,
        Dictionary<string, AttributeValue> account,
        Dictionary<string, AttributeValue> entryKey,
        Dictionary<string, AttributeValue>? entry,
        string after)
    {
        var got = (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Item(("id", account["id"])), ConsistentRead = true })).Item;
        Ensure(Py.Equal(got, account), $"after {after}: expected the account {Py.Repr(account)}, got {Py.Repr(got)}");
        var gotEntry = (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = entryKey, ConsistentRead = true })).Item;
        Ensure(Py.Equal(gotEntry, entry), $"after {after}: expected the ledger entry {Py.Repr(entry)}, got {Py.Repr(gotEntry)}");
    }

    private static async Task DeleteTableIfExists(IAmazonDynamoDB ddb, string table)
    {
        var error = await FidelityContext.CaptureError(() => ddb.DeleteTableAsync(table));
        if (error is not null && error.Code != "ResourceNotFoundException")
        {
            throw error.Exception;
        }
    }

    /// <summary>Grade a ClientError on code, HTTP status class and a non-empty message.</summary>
    private static void AssertError(AwsError exc, string[] codes, int status = 400)
    {
        var code = exc.Code;
        var msg = exc.Message ?? "";
        var gotStatus = (int)exc.StatusCode;
        Ensure(codes.Contains(code), $"expected error code in {Py.Repr(codes)}, got {Py.Repr(code)}: {Py.Repr(msg)}");
        Ensure(gotStatus / 100 == status / 100, $"expected HTTP {status / 100}xx with {code}, got {Py.Repr(gotStatus)}");
        Ensure(msg.Trim().Length > 0, $"expected a non-empty message with {code}");
    }

    /// <summary>Call fn() and grade the ClientError it raises; an accepted call is itself a failure.</summary>
    private static async Task<AwsError> ExpectError<T>(Func<Task<T>> fn, string[] codes, int status = 400)
        where T : class
    {
        var (result, err) = await FidelityContext.Capture(fn);
        if (err is not null)
        {
            AssertError(err, codes, status);
            return err;
        }

        throw Fail($"expected {Py.Repr(codes)}, but the call succeeded: {result}");
    }

    /// <summary>Poll DescribeTable until the table is ACTIVE; on expiry fail with the last status read.</summary>
    private static async Task WaitTableActive(IAmazonDynamoDB ddb, string table, double timeoutS = WaitS)
    {
        var clock = FidelityTime.StartNew();
        while (true)
        {
            var status = (await ddb.DescribeTableAsync(table)).Table.TableStatus;
            if (status == TableStatus.ACTIVE)
            {
                return;
            }

            if (clock.Elapsed.TotalSeconds >= timeoutS)
            {
                throw Fail($"table {table} not ACTIVE within {timeoutS.ToString("F0", CultureInfo.InvariantCulture)}s; last TableStatus {Py.Repr(status)}");
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(1.0));
        }
    }
}
