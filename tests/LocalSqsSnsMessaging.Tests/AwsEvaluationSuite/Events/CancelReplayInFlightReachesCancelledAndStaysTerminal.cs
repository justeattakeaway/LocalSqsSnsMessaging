// Translated from behavioral/events/check_cancel_replay_in_flight_reaches_cancelled_and_stays_terminal.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.EventBridge;
using Amazon.EventBridge.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Events;

/// <summary>events.cancel-replay-in-flight-reaches-cancelled-and-stays-terminal</summary>
[Category("AwsEvaluationSuite")]
public sealed class CancelReplayInFlightReachesCancelledAndStaysTerminal
{
    private const string Check = "cancel-replay-in-flight-reaches-cancelled-and-stays-terminal";
    private const double ArchiveTimeoutS = 90.0;
    private const double ReplayTimeoutS = 240.0;
    private const int CancelAttempts = 3;
    private static readonly string[] Terminal = ["CANCELLED", "COMPLETED", "FAILED"];

    /// <summary>
    /// CancelReplay on a just-started replay answers CANCELLING and it settles CANCELLED with a
    /// ReplayEndTime, as ListReplays agrees; it cannot be cancelled again and its name stays taken.
    /// </summary>
    [Test]
    [DisplayName("events.cancel-replay-in-flight-reaches-cancelled-and-stays-terminal")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var events = ctx.Events;
        var bus = FidelityContext.UniqueName("sim2real-behav-bus-");
        var busArn = (await events.CreateEventBusAsync(new CreateEventBusRequest { Name = bus, Tags = [new Tag { Key = "sim2real", Value = Check }] })).EventBusArn;
        ctx.OnCleanup(() => events.DeleteEventBusAsync(new DeleteEventBusRequest { Name = bus }));

        var archive = FidelityContext.UniqueName("sim2real-behav-arch-");
        var archiveArn = (await events.CreateArchiveAsync(new CreateArchiveRequest { ArchiveName = archive, EventSourceArn = busArn })).ArchiveArn;
        ctx.OnCleanup(() => events.DeleteArchiveAsync(new DeleteArchiveRequest { ArchiveName = archive }));
        var state = await Poll(
            async () => (await events.DescribeArchiveAsync(new DescribeArchiveRequest { ArchiveName = archive })).State?.Value,
            s => s is "ENABLED" or "CREATE_FAILED", ArchiveTimeoutS, "archive settles", ctx);
        Ensure(state == "ENABLED", $"archive must be ENABLED before replaying, got '{state}'");

        var now = FidelityTime.UtcNow.UtcDateTime;
        var windowStart = now - TimeSpan.FromHours(1);
        var windowEnd = now;
        StartReplayRequest Start(string name) => new()
        {
            ReplayName = name, EventSourceArn = archiveArn, Destination = new ReplayDestination { Arn = busArn },
            EventStartTime = windowStart, EventEndTime = windowEnd,
        };

        // A replay over an empty window may finish before CancelReplay arrives, and CancelReplay on a
        // finished replay is refused; only a replay still in flight is graded, so a fresh one is
        // started until the cancel lands while it is live.
        string replay = null!;
        StartReplayResponse started = null!;
        CancelReplayResponse cancelled = null!;
        for (var attempt = 1; attempt <= CancelAttempts; attempt++)
        {
            replay = FidelityContext.UniqueName("sim2real-behav-rp-");
            started = await events.StartReplayAsync(Start(replay));
            var name = replay;
            ctx.OnCleanup(() => CancelIfLive(events, name));
            Ensure(
                started.ReplayArn.EndsWith($":replay/{replay}", StringComparison.Ordinal),
                $"StartReplay returned ReplayArn '{started.ReplayArn}', expected one ending in :replay/{replay}");
            var (response, err) = await FidelityContext.Capture(() => events.CancelReplayAsync(new CancelReplayRequest { ReplayName = replay }));
            if (err is null)
            {
                cancelled = response!;
                break;
            }

            var refusedAsFinished =
                err.Code == "IllegalStatusException"
                && err.Message == $"Replay {replay} is not in a valid state for this operation."
                && (await events.DescribeReplayAsync(new DescribeReplayRequest { ReplayName = replay })).State?.Value == "COMPLETED";
            if (!refusedAsFinished || attempt == CancelAttempts)
            {
                throw new FidelityAssertionException(
                    $"CancelReplay on a just-started replay must answer CANCELLING; attempt {attempt} " +
                    $"was refused with '{err.Code}' '{err.Message}'", err.Exception);
            }
        }

        Ensure(
            cancelled.State?.Value == "CANCELLING",
            $"CancelReplay on a just-started replay must answer CANCELLING, got '{cancelled.State?.Value}'");
        Ensure(
            cancelled.ReplayArn == started.ReplayArn,
            "CancelReplay must name the same replay ARN StartReplay returned");

        // Cancellation is asynchronous: only the terminal state shows the cancel was not dropped.
        var final = await Poll(
            () => events.DescribeReplayAsync(new DescribeReplayRequest { ReplayName = replay }),
            d => Terminal.Contains(d.State?.Value), ReplayTimeoutS, "replay reaches a terminal state",
            ctx);
        Ensure(
            final.State?.Value == "CANCELLED",
            $"a cancelled replay must end CANCELLED, got '{final.State?.Value}' ('{final.StateReason}')");
        Ensure(final.ReplayEndTime is not null, $"a CANCELLED replay must carry ReplayEndTime: {final}");
        Ensure(
            final.EventSourceArn == archiveArn && final.Destination?.Arn == busArn,
            "cancelling must not alter the replay's source or destination");

        var listed = await ListReplayStates(events, replay);
        Ensure(
            listed.Count == 1 && listed.GetValueOrDefault(replay) == "CANCELLED",
            $"ListReplays(NamePrefix=...) must agree on CANCELLED, got {{{string.Join(", ", listed.Select(kv => $"'{kv.Key}': '{kv.Value}'"))}}}");

        var (code, message) = await Error(() => events.CancelReplayAsync(new CancelReplayRequest { ReplayName = replay }));
        Ensure(
            (code, message) == ("IllegalStatusException", $"Replay {replay} is not in a valid state for this operation."),
            $"cancelling a CANCELLED replay must be refused, got '{code}' '{message}'");

        (code, message) = await Error(() => events.StartReplayAsync(Start(replay)));
        Ensure(
            (code, message) == ("ResourceAlreadyExistsException", $"Replay {replay} already exists."),
            $"a cancelled replay's name stays taken; StartReplay under it gave '{code}' '{message}'");
        Ensure(
            (await events.DescribeReplayAsync(new DescribeReplayRequest { ReplayName = replay })).State?.Value == "CANCELLED",
            "the refused StartReplay must leave the cancelled replay untouched");
    }

    /// <summary>
    /// A poll's timeout: cap_s, bounded by the runner's remaining time less 5 s so the poll fails
    /// with its own message.
    /// </summary>
    private static double BudgetS(FidelityContext ctx, double capS) => Math.Max(1.0, Math.Min(capS, ctx.TimeLeftSeconds - 5.0));

    /// <summary>True when the runner's remaining time, not the wait's own deadline, ended a capped wait.</summary>
    private static bool BudgetSpent(FidelityContext ctx) => ctx.TimeLeftSeconds <= 6.0;

    private static async Task<T> Poll<T>(Func<Task<T>> read, Func<T, bool> done, double timeoutS, string what, FidelityContext ctx)
    {
        timeoutS = BudgetS(ctx, timeoutS);
        var deadline = FidelityTime.StartNew();
        T? value = default;
        while (deadline.Elapsed < TimeSpan.FromSeconds(timeoutS))
        {
            value = await read();
            if (done(value))
            {
                return value;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(2));
        }

        if (BudgetSpent(ctx))
        {
            throw new TimeoutException($"{what}: the run budget ended first; not graded");
        }

        throw Fail($"timed out after {timeoutS}s waiting until {what}; last read {value}");
    }

    private static async Task<Dictionary<string, string?>> ListReplayStates(IAmazonEventBridge events, string prefix)
    {
        var states = new Dictionary<string, string?>(StringComparer.Ordinal);
        string? token = null;
        while (true)
        {
            var resp = await events.ListReplaysAsync(new ListReplaysRequest { NamePrefix = prefix, NextToken = string.IsNullOrEmpty(token) ? null : token });
            foreach (var r in resp.Replays ?? [])
            {
                states[r.ReplayName] = r.State?.Value;
            }

            token = resp.NextToken;
            if (string.IsNullOrEmpty(token))
            {
                return states;
            }
        }
    }

    /// <summary>Teardown: a replay still STARTING or RUNNING is cancelled so it does not outlive the run.</summary>
    private static async Task CancelIfLive(IAmazonEventBridge events, string name)
    {
        if ((await events.DescribeReplayAsync(new DescribeReplayRequest { ReplayName = name })).State?.Value is "STARTING" or "RUNNING")
        {
            await events.CancelReplayAsync(new CancelReplayRequest { ReplayName = name });
        }
    }

    /// <summary>(code, message) of a call that must fail; an unexpected success raises, naming the result.</summary>
    private static async Task<(string? Code, string? Message)> Error<T>(Func<Task<T>> fn)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is null)
        {
            throw Fail($"expected the call to be refused, but it succeeded: {result}");
        }

        return (error.Code, error.Message);
    }
}
