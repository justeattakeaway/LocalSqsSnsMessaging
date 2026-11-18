// Translated from behavioral/dynamodb/check_conc_ddb_optimistic_loser_all_old_is_winner_post_image_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Globalization;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.conc-ddb-optimistic-loser-all-old-is-winner-post-image-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class ConcDdbOptimisticLoserAllOldIsWinnerPostImageRejected
{
    private const string Check = "conc-ddb-optimistic-loser-all-old-is-winner-post-image-rejected";
    private const int Racers = 8;
    private const string ErrCcf = "ConditionalCheckFailedException";
    private const string CcfMessage = "The conditional request failed";

    /// <summary>
    /// Each loser of an eight-way version race gets ConditionalCheckFailedException whose ALL_OLD
    /// Item is the winner's post-image, and can retry from it alone; without the parameter a refusal
    /// carries no Item, and refusals never change the item.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.conc-ddb-optimistic-loser-all-old-is-winner-post-image-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = await MakeTable(ctx);
        Dictionary<string, AttributeValue> Key() => Item(("pk", S("cart")));
        await ddb.PutItemAsync(table, Item(("pk", S("cart")), ("version", N("0")), ("writer", S("seed"))));

        Func<Task<UpdateItemResponse>> Update(int expected, string me, bool allOld = true, bool returnNew = false)
        {
            var request = new UpdateItemRequest
            {
                TableName = table,
                Key = Key(),
                UpdateExpression = "SET version = :next, writer = :me",
                ConditionExpression = "version = :expected",
                ExpressionAttributeValues = Item(
                    (":expected", N(expected.ToString(CultureInfo.InvariantCulture))),
                    (":next", N((expected + 1).ToString(CultureInfo.InvariantCulture))),
                    (":me", S(me))),
            };
            if (allOld)
            {
                request.ReturnValuesOnConditionCheckFailure = ReturnValuesOnConditionCheckFailure.ALL_OLD;
            }

            if (returnNew)
            {
                request.ReturnValues = ReturnValue.ALL_NEW;
            }

            return () => ddb.UpdateItemAsync(request);
        }

        // --- the race: everyone read version 0 -----------------------------------------------------
        var ids = Enumerable.Range(0, Racers).Select(i => $"w{i}").ToList();
        var barrier = new AsyncBarrier(Racers);

        async Task<(UpdateItemResponse? Response, AwsError? Error)> Racer(string me)
        {
            await barrier.SignalAndWait(cancellationToken);
            return await FidelityContext.Capture(Update(0, me, returnNew: true));
        }

        (UpdateItemResponse? Response, AwsError? Error)[] outcomes;
        using (ctx.Concurrently())
        {
            outcomes = await Task.WhenAll(ids.Select(me => Task.Run(() => Racer(me), cancellationToken)));
        }

        var winners = ids.Zip(outcomes).Where(p => p.Second.Error is null).Select(p => (Id: p.First, Resp: p.Second.Response!)).ToList();
        var losers = ids.Zip(outcomes).Where(p => p.Second.Error is not null).Select(p => (Id: p.First, Error: p.Second.Error!)).ToList();
        Ensure(winners.Count == 1, $"{winners.Count} winners {Py.Repr(winners.Select(w => w.Id))}; exactly one may win");
        var (winnerId, winnerResp) = winners[0];
        var postImage = winnerResp.Attributes;
        Ensure(
            Py.Equal(postImage, Item(("pk", S("cart")), ("version", N("1")), ("writer", S(winnerId)))),
            $"winner ALL_NEW {Py.Repr(postImage)} is not version 1 written by {Py.Repr(winnerId)}");
        Ensure(
            losers.Count == Racers - 1 && losers.All(l => l.Error.Code == ErrCcf),
            $"losers must each get {ErrCcf}; got {Py.Repr(losers.Select(l => l.Error.Code))}");
        Ensure(
            losers.All(l => l.Error.Message == CcfMessage),
            $"losers must each get the message {Py.Repr(CcfMessage)}; got {Py.Repr(losers.Select(l => l.Error.Message))}");
        foreach (var (me, error) in losers)
        {
            Ensure(
                Py.Equal(CcfItem(error), postImage),
                $"loser {Py.Repr(me)} ALL_OLD Item {Py.Repr(CcfItem(error))} is not the winner's post-image " +
                $"{Py.Repr(postImage)} -- the recovery payload must be the state that beat it");
        }

        // --- effect absent: no parameter, no Item; and neither refusal changed anything -------------
        var bare = await FidelityContext.Capture(Update(0, "stale-no-param", allOld: false));
        Ensure(bare.Error is not null && bare.Error.Code == ErrCcf, $"stale write without ALL_OLD: {Py.Repr(bare.Error?.Code ?? "ok")}");
        Ensure(bare.Error!.Message == CcfMessage, $"stale write message: {bare.Error}");
        Ensure(
            CcfItem(bare.Error) is null,
            $"a CCF without ReturnValuesOnConditionCheckFailure must carry no Item; got {Py.Repr(CcfItem(bare.Error))}");
        var now = (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Key(), ConsistentRead = true })).Item;
        Ensure(Py.Equal(now, postImage), $"refused writes changed the item: {Py.Repr(now)} != {Py.Repr(postImage)}");

        // --- recovery: each loser retries from the image it holds, never reading ---------------------
        foreach (var (me, error) in losers)
        {
            var held = CcfItem(error)!;
            var landed = false;
            for (var attemptNo = 0; attemptNo < 2; attemptNo++)
            {
                // a stale image costs exactly one more refusal, never more
                var res = await FidelityContext.Capture(Update(int.Parse(held["version"].N, CultureInfo.InvariantCulture), me));
                if (res.Error is null)
                {
                    landed = true;
                    break;
                }

                Ensure(res.Error.Code == ErrCcf, $"loser {Py.Repr(me)} retry got {Py.Repr(res.Error.Code)}, expected {ErrCcf}");
                held = CcfItem(res.Error)!;
                Ensure(held is not null, $"loser {Py.Repr(me)} retry CCF carried no ALL_OLD Item: {res.Error}");
            }

            Ensure(landed, $"loser {Py.Repr(me)} could not land from its ALL_OLD image within 2 attempts");
        }

        var final = (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Key(), ConsistentRead = true })).Item;
        Ensure(
            Py.Equal(final["version"], N(Racers.ToString(CultureInfo.InvariantCulture))),
            $"final version {Py.Repr(final["version"])}; 1 winner + {losers.Count} recovered losers = {Racers}");
        Ensure(
            Py.Equal(final["writer"], S(losers[^1].Id)),
            $"final writer {Py.Repr(final["writer"])}, expected the last loser to recover {Py.Repr(losers[^1].Id)}");
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
