// Translated from behavioral/dynamodb/check_transact_write_skew_on_absent_keys_never_commits_both.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Globalization;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.transact-write-skew-on-absent-keys-never-commits-both</summary>
[Category("AwsEvaluationSuite")]
public sealed class TransactWriteSkewOnAbsentKeysNeverCommitsBoth
{
    private const int Rounds = 15;
    private const string CancelledPrefix = "Transaction cancelled, please refer cancellation reasons for specific reasons ";
    private static readonly HashSet<string> ReasonCodes = new(StringComparer.Ordinal) { "None", "ConditionalCheckFailed", "TransactionConflict" };

    /// <summary>A cleanup running undo() where a failed delete raises; in-use is retried every 2 s.</summary>
    private static Func<Task> Teardown(Func<Task> undo, string notFound, double timeoutSeconds = 300.0) => async () =>
    {
        var clock = FidelityTime.StartNew();
        while (true)
        {
            var error = await FidelityContext.CaptureError(undo);
            if (error is null || error.Code == notFound)
            {
                return;
            }

            if (error.Code != "ResourceInUseException" || clock.Elapsed.TotalSeconds >= timeoutSeconds)
            {
                throw error.Exception;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(2));
        }
    };

    /// <summary>Poll DescribeTable every 2 s until the table and its indexes are ACTIVE.</summary>
    private static async Task<TableDescription> WaitActive(IAmazonDynamoDB ddb, FidelityContext ctx, string table)
    {
        var timeLeft = ctx.TimeLeftSeconds;
        var deadline = Math.Max(0.0, (timeLeft > 0 ? timeLeft : 300.0) - 30.0);
        var clock = FidelityTime.StartNew();
        TableDescription? desc = null;
        while (clock.Elapsed.TotalSeconds < deadline)
        {
            desc = (await ddb.DescribeTableAsync(table)).Table;
            if (desc.TableStatus == TableStatus.ACTIVE
                && (desc.GlobalSecondaryIndexes ?? []).All(g => g.IndexStatus == IndexStatus.ACTIVE))
            {
                return desc;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(2));
        }

        throw Fail($"table {table} not ACTIVE before the deadline: {desc}");
    }

    /// <summary>Create an on-demand table keyed pk (S); wait ACTIVE.</summary>
    private static async Task<string> CreateTable(FidelityContext ctx, IAmazonDynamoDB ddb, string prefix)
    {
        var table = FidelityContext.UniqueName(prefix);
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
        });
        ctx.OnCleanup(Teardown(() => ddb.DeleteTableAsync(table), "ResourceNotFoundException"));
        await WaitActive(ddb, ctx, table);
        return table;
    }

    /// <summary>The item at key read with ConsistentRead, or null.</summary>
    private static async Task<Dictionary<string, AttributeValue>?> Get(IAmazonDynamoDB ddb, string table, Dictionary<string, AttributeValue> key) =>
        (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = key, ConsistentRead = true })).Item;

    /// <summary>threading.Barrier(2): every party waits until all have arrived.</summary>
    private sealed class AsyncBarrier(int parties)
    {
        private readonly TaskCompletionSource _all = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _arrived;

        public Task SignalAndWait()
        {
            if (Interlocked.Increment(ref _arrived) == parties)
            {
                _all.TrySetResult();
            }

            return _all.Task.WaitAsync(TimeSpan.FromSeconds(60));
        }
    }

    private static string Reasons(List<CancellationReason>? reasons) =>
        reasons is null
            ? "None"
            : "[" + string.Join(", ", reasons.Select(r =>
            {
                var d = new Dictionary<string, object?>(StringComparer.Ordinal) { ["Code"] = r.Code };
                if (r.Message is not null)
                {
                    d["Message"] = r.Message;
                }

                return Py.Repr(d);
            })) + "]";

    /// <summary>
    /// Two concurrent transactions that each check the other's absent key and write their own never
    /// both commit and at least one commits; each failure is TransactionCanceledException naming a
    /// failing item, and only committed writes are stored.
    ///
    /// Reference: https://docs.aws.amazon.com/amazondynamodb/latest/developerguide/transaction-apis.html
    /// </summary>
    [Test]
    [DisplayName("dynamodb.transact-write-skew-on-absent-keys-never-commits-both")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = await CreateTable(ctx, ddb, "sim2real-behav-ddb-txskew-");

        // Run [ConditionCheck absent(check_pk), Put put_pk]; return None or (code, msg, reasons).
        async Task<(string? Code, string? Message, List<CancellationReason>? Reasons)?> Attempt(AsyncBarrier barrier, string checkPk, string putPk)
        {
            await barrier.SignalAndWait();
            var error = await FidelityContext.CaptureError(() => ddb.TransactWriteItemsAsync(new TransactWriteItemsRequest
            {
                TransactItems =
                [
                    new TransactWriteItem
                    {
                        ConditionCheck = new ConditionCheck
                        {
                            TableName = table,
                            Key = Item(("pk", S(checkPk))),
                            ConditionExpression = "attribute_not_exists(pk)",
                        },
                    },
                    new TransactWriteItem { Put = new Put { TableName = table, Item = Item(("pk", S(putPk))) } },
                ],
            }));
            return error is null ? null : (error.Code, error.Message, error.CancellationReasons());
        }

        var committed = new List<string>();
        for (var r = 0; r < Rounds; r++)
        {
            var (x, y) = ($"x-{r.ToString("D2", CultureInfo.InvariantCulture)}", $"y-{r.ToString("D2", CultureInfo.InvariantCulture)}");
            var barrier = new AsyncBarrier(2);
            (string? Code, string? Message, List<CancellationReason>? Reasons)?[] results;
            using (ctx.Concurrently())
            {
                var fx = Task.Run(() => Attempt(barrier, y, x), cancellationToken);
                var fy = Task.Run(() => Attempt(barrier, x, y), cancellationToken);
                results = await Task.WhenAll(fx, fy);
            }

            var outcomes = new List<(string Pk, (string? Code, string? Message, List<CancellationReason>? Reasons)? Outcome)>
            {
                (x, results[0]),
                (y, results[1]),
            };
            Ensure(
                outcomes.Any(o => o.Outcome is not null),
                $"round {r}: both transactions committed, each having seen the other's key absent");
            foreach (var (pk, outcome) in outcomes)
            {
                var stored = await Get(ddb, table, Item(("pk", S(pk))));
                if (outcome is null)
                {
                    Ensure(Py.Equal(stored, Item(("pk", S(pk)))), $"round {r}: committed Put {Py.Repr(pk)} reads {Py.Repr(stored)}");
                    committed.Add(pk);
                    continue;
                }

                var (code, msg, reasons) = outcome.Value;
                var codes = (reasons ?? []).Select(reason => reason.Code).ToList();
                var expected = CancelledPrefix + "[" + string.Join(", ", codes.Select(c => c ?? "None")) + "]";
                Ensure(
                    code == "TransactionCanceledException" && msg == expected,
                    $"round {r}: the transaction writing {Py.Repr(pk)} failed with {Py.Repr(code)}: {Py.Repr(msg)}");
                Ensure(
                    codes.Count == 2 && codes.All(c => c is not null && ReasonCodes.Contains(c)),
                    $"round {r}: the cancellation of {Py.Repr(pk)} carried reasons {Reasons(reasons)}");
                Ensure(
                    codes.Any(c => c != "None"),
                    $"round {r}: the cancellation of {Py.Repr(pk)} names no failing item: {Reasons(reasons)}");
                Ensure(stored is null, $"round {r}: cancelled Put {Py.Repr(pk)} was stored as {Py.Repr(stored)}");
            }
        }

        Ensure(committed.Count > 0, $"no transaction committed in {Rounds} rounds: {Py.Repr(committed)}");
    }
}
