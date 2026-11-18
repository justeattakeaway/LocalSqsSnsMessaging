// Translated from behavioral/dynamodb/check_conc_ddb_optimistic_version_race_one_winner_per_round.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Globalization;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.conc-ddb-optimistic-version-race-one-winner-per-round</summary>
[Category("AwsEvaluationSuite")]
public sealed class ConcDdbOptimisticVersionRaceOneWinnerPerRound
{
    private const string Check = "conc-ddb-optimistic-version-race-one-winner-per-round";
    private const int Rounds = 5;
    private const int Racers = 8;
    private const string ErrCcf = "ConditionalCheckFailedException";

    /// <summary>
    /// Eight writers racing a version-guarded UpdateItem yield one winner per round, each loser gets
    /// ConditionalCheckFailedException, and after five rounds the version and history match exactly
    /// the winners, in order: no lost update and no phantom increment.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.conc-ddb-optimistic-version-race-one-winner-per-round")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = await MakeTable(ctx);
        Dictionary<string, AttributeValue> Key() => Item(("pk", S("account")));
        await ddb.PutItemAsync(table, Item(("pk", S("account")), ("version", N("0")), ("writer", S("seed")), ("history", L())));

        Func<Task> Bump(int expected, string me) => () => ddb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = table,
            Key = Key(),
            UpdateExpression = "SET version = :next, writer = :me, history = list_append(history, :me_l)",
            ConditionExpression = "version = :expected",
            ExpressionAttributeValues = Item(
                (":expected", N(expected.ToString(CultureInfo.InvariantCulture))),
                (":next", N((expected + 1).ToString(CultureInfo.InvariantCulture))),
                (":me", S(me)),
                (":me_l", L(S(me)))),
        });

        var winners = new List<string>();
        for (var rnd = 0; rnd < Rounds; rnd++)
        {
            var before = (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Key(), ConsistentRead = true })).Item;
            var expected = int.Parse(before["version"].N, CultureInfo.InvariantCulture);
            Ensure(expected == rnd, $"round {rnd}: version before the race is {Py.Repr(expected)}, expected {rnd}");
            var ids = Enumerable.Range(0, Racers).Select(i => $"r{rnd}-w{i}").ToList();
            AwsError?[] outcomes;
            using (ctx.Concurrently())
            {
                outcomes = await Race([.. ids.Select(me => Bump(expected, me))], cancellationToken);
            }

            var won = ids.Zip(outcomes).Where(p => p.Second is null).Select(p => p.First).ToList();
            var lostCodes = outcomes.Where(o => o is not null).Select(o => o!.Code).ToList();
            Ensure(
                won.Count == 1,
                $"round {rnd}: {won.Count} writers were told they won a version={expected} " +
                $"compare-and-set (winners={Py.Repr(won)}); exactly one may win");
            Ensure(
                lostCodes.Count == Racers - 1 && lostCodes.All(c => c == ErrCcf),
                $"round {rnd}: losers must each get {ErrCcf}; got {Py.Repr(lostCodes)}");

            var after = (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Key(), ConsistentRead = true })).Item;
            Ensure(
                Py.Equal(after["version"], N((expected + 1).ToString(CultureInfo.InvariantCulture))),
                $"round {rnd}: version after one winning write is {Py.Repr(after["version"])}, " +
                $"expected {expected + 1}");
            Ensure(
                Py.Equal(after["writer"], S(won[0])),
                $"round {rnd}: item names writer {Py.Repr(after["writer"])} but the caller told it won " +
                $"was {Py.Repr(won[0])} -- a loser's write landed");
            winners.Add(won[0]);
        }

        var final = (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Key(), ConsistentRead = true })).Item;
        var finalVersion = int.Parse(final["version"].N, CultureInfo.InvariantCulture);
        Ensure(
            finalVersion == winners.Count && winners.Count == Rounds,
            $"final version {Py.Repr(final["version"])} vs {winners.Count} reported successes over {Rounds} rounds");
        var history = final["history"].L.Select(e => e.S).ToList();
        Ensure(
            history.SequenceEqual(winners),
            $"history {Py.Repr(history)} must equal exactly the reported winners {Py.Repr(winners)} -- " +
            "an extra entry is a phantom increment, a missing one is a lost update");
    }

    /// <summary>
    /// Run every fn at once behind one Barrier; return None (ok) or the error per fn.
    /// A non-ClientError propagates, so a harness failure is never counted as a losing racer.
    /// </summary>
    private static async Task<AwsError?[]> Race(IReadOnlyList<Func<Task>> fns, CancellationToken cancellationToken)
    {
        var barrier = new AsyncBarrier(fns.Count);

        async Task<AwsError?> One(Func<Task> fn)
        {
            await barrier.SignalAndWait(cancellationToken);
            return await FidelityContext.CaptureError(fn);
        }

        return await Task.WhenAll(fns.Select(fn => Task.Run(() => One(fn), cancellationToken)));
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
