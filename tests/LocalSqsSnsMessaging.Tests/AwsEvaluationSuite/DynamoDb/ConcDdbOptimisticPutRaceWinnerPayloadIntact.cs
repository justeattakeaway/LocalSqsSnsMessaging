// Translated from behavioral/dynamodb/check_conc_ddb_optimistic_put_race_winner_payload_intact.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Globalization;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Ddb;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.conc-ddb-optimistic-put-race-winner-payload-intact</summary>
[Category("AwsEvaluationSuite")]
public sealed class ConcDdbOptimisticPutRaceWinnerPayloadIntact
{
    private const string Check = "conc-ddb-optimistic-put-race-winner-payload-intact";
    private const int Rounds = 4;
    private const int Racers = 8;
    private const string ErrCcf = "ConditionalCheckFailedException";

    /// <summary>
    /// Eight writers racing a version-guarded full-item PutItem leave one winner per round, and the
    /// item holds exactly the winner's attributes: none from a loser or the previous round's winner.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.conc-ddb-optimistic-put-race-winner-payload-intact")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = await MakeTable(ctx);
        await ddb.PutItemAsync(table, Item(("pk", S("doc")), ("version", N("0")), ("writer", S("seed")), ("only_seed", S("seed"))));

        Func<Task> Put(int expected, string me)
        {
            var item = Item(
                ("pk", S("doc")),
                ("version", N((expected + 1).ToString(CultureInfo.InvariantCulture))),
                ("writer", S(me)),
                ($"only_{me}", S(me)));
            return () => ddb.PutItemAsync(new PutItemRequest
            {
                TableName = table,
                Item = item,
                ConditionExpression = "version = :expected",
                ExpressionAttributeValues = Item((":expected", N(expected.ToString(CultureInfo.InvariantCulture)))),
            });
        }

        var successes = 0;
        for (var rnd = 0; rnd < Rounds; rnd++)
        {
            var expected = int.Parse(
                (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Item(("pk", S("doc"))), ConsistentRead = true })).Item["version"].N,
                CultureInfo.InvariantCulture);
            Ensure(expected == rnd, $"round {rnd}: version before the race is {Py.Repr(expected)}, expected {rnd}");
            var ids = Enumerable.Range(0, Racers).Select(i => $"r{rnd}w{i}").ToList();
            AwsError?[] outcomes;
            using (ctx.Concurrently())
            {
                outcomes = await Race([.. ids.Select(me => Put(expected, me))], cancellationToken);
            }

            var won = ids.Zip(outcomes).Where(p => p.Second is null).Select(p => p.First).ToList();
            var lostCodes = outcomes.Where(o => o is not null).Select(o => o!.Code).ToList();
            Ensure(won.Count == 1, $"round {rnd}: winners {Py.Repr(won)}; exactly one conditional PutItem may win");
            Ensure(
                lostCodes.Count == Racers - 1 && lostCodes.All(c => c == ErrCcf),
                $"round {rnd}: losers must each get {ErrCcf}; got {Py.Repr(lostCodes)}");
            successes++;

            var item = (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Item(("pk", S("doc"))), ConsistentRead = true })).Item;
            Ensure(
                item.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(["pk", "version", "writer", $"only_{won[0]}"]),
                $"round {rnd}: item attributes {Py.Repr(item.Keys.Order(StringComparer.Ordinal).ToList())} must be exactly the winner {Py.Repr(won[0])}'s " +
                "payload -- anything else is a loser's or a stale attribute surviving");
            Ensure(
                Py.Equal(item["writer"], S(won[0])) && Py.Equal(item["version"], N((expected + 1).ToString(CultureInfo.InvariantCulture))),
                $"round {rnd}: item {Py.Repr(item)} is not the winner {Py.Repr(won[0])}'s version {expected + 1}");
        }

        var final = (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Item(("pk", S("doc"))), ConsistentRead = true })).Item;
        var finalVersion = int.Parse(final["version"].N, CultureInfo.InvariantCulture);
        Ensure(
            finalVersion == successes && successes == Rounds,
            $"final version {Py.Repr(final["version"])}, {successes} successes, {Rounds} rounds");
    }

    /// <summary>Run every fn at the same instant (one Barrier); return None (ok) / the error each.</summary>
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
