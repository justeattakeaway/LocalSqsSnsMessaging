// Translated from behavioral/dynamodb/check_conc_ddb_optimistic_transact_race_cancelled_leaves_no_partial_write.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Globalization;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Ddb;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.conc-ddb-optimistic-transact-race-cancelled-leaves-no-partial-write</summary>
[Category("AwsEvaluationSuite")]
public sealed class ConcDdbOptimisticTransactRaceCancelledLeavesNoPartialWrite
{
    private const string Check = "conc-ddb-optimistic-transact-race-cancelled-leaves-no-partial-write";
    private const int Rounds = 3;
    private const int Racers = 8;
    private const string ErrCanceled = "TransactionCanceledException";

    /// <summary>
    /// Eight TransactWriteItems racing on a version-guarded item commit at most once per round; each
    /// cancelled one has positional CancellationReasons (only the contended slot non-None) and leaves
    /// its private item unwritten. The version advances by exactly the number of commits.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.conc-ddb-optimistic-transact-race-cancelled-leaves-no-partial-write")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = await MakeTable(ctx);
        await ddb.PutItemAsync(table, Item(("pk", S("shared")), ("version", N("0"))));

        Func<Task<TransactWriteItemsResponse>> Tx(int expected, string privatePk) => () => ddb.TransactWriteItemsAsync(new TransactWriteItemsRequest
        {
            TransactItems =
            [
                new TransactWriteItem
                {
                    Update = new Update
                    {
                        TableName = table,
                        Key = Item(("pk", S("shared"))),
                        UpdateExpression = "SET version = :next, last_tx = :me",
                        ConditionExpression = "version = :expected",
                        ExpressionAttributeValues = Item(
                            (":expected", N(expected.ToString(CultureInfo.InvariantCulture))),
                            (":next", N((expected + 1).ToString(CultureInfo.InvariantCulture))),
                            (":me", S(privatePk))),
                    },
                },
                new TransactWriteItem
                {
                    Put = new Put { TableName = table, Item = Item(("pk", S(privatePk))), ConditionExpression = "attribute_not_exists(pk)" },
                },
                new TransactWriteItem
                {
                    ConditionCheck = new ConditionCheck
                    {
                        TableName = table,
                        Key = Item(("pk", S($"anchor-{privatePk}"))),
                        ConditionExpression = "attribute_exists(pk)",
                    },
                },
            ],
        });

        async Task<bool> Exists(string pk) =>
            (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Item(("pk", S(pk))), ConsistentRead = true })).Item is not null;

        async Task<int> Version() => int.Parse(
            (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Item(("pk", S("shared"))), ConsistentRead = true })).Item["version"].N,
            CultureInfo.InvariantCulture);

        // --- positive control: uncontended, the same shape commits every member ----------------------
        await ddb.PutItemAsync(table, Item(("pk", S("anchor-private-control"))));
        var ctl = await FidelityContext.Capture(Tx(0, "private-control"));
        Ensure(ctl.Error is null, $"uncontended control transaction failed: {ctl.Error}");
        Ensure(
            await Exists("private-control") && await Version() == 1,
            $"control commit did not land both writes: private={Py.Repr(await Exists("private-control"))} " +
            $"version={Py.Repr(await Version())}");

        var commits = 1;
        var seenContendedCodes = new HashSet<string>(StringComparer.Ordinal);
        for (var rnd = 0; rnd < Rounds; rnd++)
        {
            var expected = await Version();
            Ensure(expected == commits, $"round {rnd}: version {Py.Repr(expected)} != commits so far {commits}");
            var privs = Enumerable.Range(0, Racers).Select(i => $"private-r{rnd}-t{i}").ToList();
            // Per-racer anchors: a ConditionCheck on an item other in-flight transactions also check
            // is itself contended and can report TransactionConflict, so it could not be asserted None.
            foreach (var pk in privs)
            {
                await ddb.PutItemAsync(table, Item(("pk", S($"anchor-{pk}"))));
            }

            var barrier = new AsyncBarrier(Racers);

            async Task<(TransactWriteItemsResponse? Response, AwsError? Error)> Racer(string pk)
            {
                await barrier.SignalAndWait(cancellationToken);
                return await FidelityContext.Capture(Tx(expected, pk));
            }

            (TransactWriteItemsResponse? Response, AwsError? Error)[] outcomes;
            using (ctx.Concurrently())
            {
                outcomes = await Task.WhenAll(privs.Select(pk => Task.Run(() => Racer(pk), cancellationToken)));
            }

            var committed = privs.Zip(outcomes).Where(p => p.Second.Error is null).Select(p => p.First).ToList();
            Ensure(
                committed.Count <= 1,
                $"round {rnd}: {Py.Repr(committed)} all committed against version={expected}; at most one can");
            foreach (var (pk, o) in privs.Zip(outcomes))
            {
                if (o.Error is null)
                {
                    Ensure(await Exists(pk), $"round {rnd}: committed transaction {Py.Repr(pk)} left no private item");
                    continue;
                }

                Ensure(o.Error.Code == ErrCanceled, $"round {rnd}: {Py.Repr(pk)} failed with {Py.Repr(o.Error.Code)}, not {ErrCanceled}");
                var reasons = o.Error.CancellationReasons();
                Ensure(
                    reasons is { Count: 3 },
                    $"round {rnd}: {Py.Repr(pk)} CancellationReasons {Py.Repr(reasons?.Select(r => r.Code))} must be 3 entries, one per member");
                var codes = reasons!.Select(r => r.Code).ToList();
                Ensure(
                    codes[1] == "None" && codes[2] == "None",
                    $"round {rnd}: {Py.Repr(pk)} reasons {Py.Repr(codes)} -- its private Put and its own anchor " +
                    "ConditionCheck were uncontended and must be positionally None");
                Ensure(
                    codes[0] is not null && codes[0] != "None",
                    $"round {rnd}: {Py.Repr(pk)} reasons {Py.Repr(codes)} -- the contended Update in slot 0 caused the " +
                    "cancellation and must carry a code");
                seenContendedCodes.Add(codes[0]);
                Ensure(
                    !await Exists(pk),
                    $"round {rnd}: cancelled transaction {Py.Repr(pk)} left its private item behind -- partial write");
            }

            commits += committed.Count;
            if (committed.Count > 0)
            {
                var shared = (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Item(("pk", S("shared"))), ConsistentRead = true })).Item;
                Ensure(
                    Py.Equal(shared["last_tx"], S(committed[0])),
                    $"round {rnd}: shared item names {Py.Repr(shared["last_tx"])}, committed was {Py.Repr(committed[0])}");
            }
        }

        Ensure(
            await Version() == commits,
            $"final version {Py.Repr(await Version())} != commits {commits} (contended codes seen: " +
            $"{Py.Repr(seenContendedCodes.Order(StringComparer.Ordinal).ToList())})");
        Ensure(seenContendedCodes.Count > 0, "no transaction was cancelled across the races -- no contention occurred");
    }

    private static async Task<string> MakeTable(FidelityContext ctx)
    {
        var ddb = ctx.Dynamo;
        var name = FidelityContext.UniqueName("sim2real-conc-ddb-opt-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = name,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real", Value = Check }],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(name));
        await ddb.WaitForTableExists(name, delaySeconds: 2, maxAttempts: 250);
        return name;
    }

    /// <summary><c>threading.Barrier</c>: every participant waits until all have arrived.</summary>
    private sealed class AsyncBarrier(int participants)
    {
        private readonly TaskCompletionSource _all = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _remaining = participants;

        public Task SignalAndWait(CancellationToken cancellationToken)
        {
            if (Interlocked.Decrement(ref _remaining) == 0)
            {
                _all.TrySetResult();
            }

            return _all.Task.WaitAsync(cancellationToken);
        }
    }
}
