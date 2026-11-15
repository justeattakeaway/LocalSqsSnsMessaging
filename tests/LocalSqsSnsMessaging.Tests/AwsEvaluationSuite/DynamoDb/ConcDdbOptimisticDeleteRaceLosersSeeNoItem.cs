// Translated from behavioral/dynamodb/check_conc_ddb_optimistic_delete_race_losers_see_no_item.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Globalization;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Ddb;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.conc-ddb-optimistic-delete-race-losers-see-no-item</summary>
[Category("AwsEvaluationSuite")]
public sealed class ConcDdbOptimisticDeleteRaceLosersSeeNoItem
{
    private const string Check = "conc-ddb-optimistic-delete-race-losers-see-no-item";
    private const int Racers = 8;
    private const string ErrCcf = "ConditionalCheckFailedException";
    private const string CcfMessage = "The conditional request failed";

    /// <summary>
    /// Eight racers on a version-guarded DeleteItem yield one deleter handed the whole item; each
    /// loser gets ConditionalCheckFailedException with no ALL_OLD Item, because the item no longer
    /// exists. Control: a stale version on a live item is refused with the item attached.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.conc-ddb-optimistic-delete-race-losers-see-no-item")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = await MakeTable(ctx);
        Dictionary<string, AttributeValue> Key() => Item(("pk", S("lease")));
        Dictionary<string, AttributeValue> Seeded() => Item(("pk", S("lease")), ("version", N("3")), ("holder", S("seed")));

        Func<Task<DeleteItemResponse>> Delete(int expected) => () => ddb.DeleteItemAsync(new DeleteItemRequest
        {
            TableName = table,
            Key = Key(),
            ConditionExpression = "version = :expected",
            ExpressionAttributeValues = Item((":expected", N(expected.ToString(CultureInfo.InvariantCulture)))),
            ReturnValues = ReturnValue.ALL_OLD,
            ReturnValuesOnConditionCheckFailure = ReturnValuesOnConditionCheckFailure.ALL_OLD,
        });

        async Task<Dictionary<string, AttributeValue>?> Get() =>
            (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Key(), ConsistentRead = true })).Item;

        // --- control: stale version against a LIVE item is refused with the item attached ----------
        await ddb.PutItemAsync(table, Seeded());
        var ctl = await FidelityContext.Capture(Delete(2));
        Ensure(ctl.Error is not null && ctl.Error.Code == ErrCcf, $"stale delete on a live item: {Py.Repr(ctl.Error?.Code ?? "ok")}");
        Ensure(ctl.Error!.Message == CcfMessage, $"stale delete message: {ctl.Error}");
        Ensure(
            Py.Equal(CcfItem(ctl.Error), Seeded()),
            $"a CCF against a live item must carry it under ALL_OLD; got {Py.Repr(CcfItem(ctl.Error))}");
        Ensure(Py.Equal(await Get(), Seeded()), $"a refused delete removed or changed the item: {Py.Repr(await Get())}");

        // --- the race: everyone holds the right version ---------------------------------------------
        var barrier = new AsyncBarrier(Racers);

        async Task<(DeleteItemResponse? Response, AwsError? Error)> Racer(int i)
        {
            await barrier.SignalAndWait(cancellationToken);
            return await FidelityContext.Capture(Delete(3));
        }

        (DeleteItemResponse? Response, AwsError? Error)[] outcomes;
        using (ctx.Concurrently())
        {
            outcomes = await Task.WhenAll(Enumerable.Range(0, Racers).Select(i => Task.Run(() => Racer(i), cancellationToken)));
        }

        var wins = outcomes.Where(o => o.Error is null).ToList();
        var losses = outcomes.Where(o => o.Error is not null).Select(o => o.Error!).ToList();
        Ensure(wins.Count == 1, $"{wins.Count} callers were told they deleted the item; exactly one may");
        Ensure(
            Py.Equal(wins[0].Response!.Attributes, Seeded()),
            $"the deleter was handed {Py.Repr(wins[0].Response!.Attributes)}, not the item it deleted {Py.Repr(Seeded())}");
        Ensure(
            losses.All(o => o.Code == ErrCcf) && losses.Count == Racers - 1,
            $"losers must each get {ErrCcf}; got {Py.Repr(losses.Select(o => o.Code))}");
        Ensure(
            losses.All(o => o.Message == CcfMessage),
            $"losers must each get the message {Py.Repr(CcfMessage)}; got {Py.Repr(losses.Select(o => o.Message))}");
        var ghosts = losses.Select(CcfItem).Where(item => item is not null).ToList();
        Ensure(
            ghosts.Count == 0,
            $"{ghosts.Count} losers were handed an ALL_OLD image of an item that was already deleted: " +
            $"{Py.Repr(ghosts.Take(2))}");
        Ensure(await Get() is null, $"item survived the winning delete: {Py.Repr(await Get())}");
    }

    /// <summary><c>exc.response.get('Item')</c> of a ConditionalCheckFailedException.</summary>
    private static Dictionary<string, AttributeValue>? CcfItem(AwsError error) => (error.Exception as ConditionalCheckFailedException)?.Item;

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
