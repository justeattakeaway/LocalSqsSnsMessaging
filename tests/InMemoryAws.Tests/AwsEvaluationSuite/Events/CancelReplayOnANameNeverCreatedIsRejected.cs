// Translated from behavioral/events/check_cancel_replay_on_a_name_never_created_is_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit abd0811).
using Amazon.EventBridge;
using Amazon.EventBridge.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.Events;

/// <summary>events.cancel-replay-on-a-name-never-created-is-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class CancelReplayOnANameNeverCreatedIsRejected
{
    private const string Check = "cancel-replay-on-a-name-never-created-is-rejected";
    private const double ArchiveTimeoutS = 90.0;
    private const double ReplayTimeoutS = 240.0;
    private const int StartAttempts = 3;

    /// <summary>
    /// CancelReplay on a name that was never created is refused as not-found, leaving a replay
    /// actually in flight untouched, and the same call against that replay cancels it.
    /// </summary>
    [Test]
    [DisplayName("events.cancel-replay-on-a-name-never-created-is-rejected")]
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
            s => s is "ENABLED" or "CREATE_FAILED", ArchiveTimeoutS, "the archive settles");
        Ensure(state == "ENABLED", $"the archive must be ENABLED before replaying, got '{state}'");

        var now = FidelityTime.UtcNow.UtcDateTime;
        var windowStart = now - TimeSpan.FromHours(1);
        var windowEnd = now;

        var missing = FidelityContext.UniqueName("sim2real-behav-rp-");
        var (code, message) = await Error(() => events.CancelReplayAsync(new CancelReplayRequest { ReplayName = missing }));
        Ensure(
            (code, message) == ("ResourceNotFoundException", $"Replay {missing} does not exist."),
            $"cancelling a replay name that was never created must be not-found, got '{code}' '{message}'");

        // An empty-window replay can finish before the cancel lands, and CancelReplay on a replay
        // that already finished is refused as IllegalStatusException rather than cancelled; retry
        // with a fresh replay name until one is caught while still in flight.
        string replay = null!;
        StartReplayResponse started = null!;
        CancelReplayResponse cancelled = null!;
        for (var attempt = 1; attempt <= StartAttempts; attempt++)
        {
            replay = FidelityContext.UniqueName("sim2real-behav-rp-");
            started = await events.StartReplayAsync(new StartReplayRequest
            {
                ReplayName = replay,
                EventSourceArn = archiveArn,
                Destination = new ReplayDestination { Arn = busArn },
                EventStartTime = windowStart,
                EventEndTime = windowEnd,
            });
            var name = replay;
            ctx.OnCleanup(() => CancelIfLive(events, name));
            var (response, err) = await FidelityContext.Capture(() => events.CancelReplayAsync(new CancelReplayRequest { ReplayName = replay }));
            if (err is null)
            {
                cancelled = response!;
                break;
            }

            var finishedFirst =
                err.Code == "IllegalStatusException"
                && (await events.DescribeReplayAsync(new DescribeReplayRequest { ReplayName = replay })).State?.Value == "COMPLETED";
            if (!finishedFirst || attempt == StartAttempts)
            {
                throw new FidelityAssertionException(
                    $"cancelling a just-started replay must succeed; attempt {attempt} was " +
                    $"refused with '{err.Code}' '{err.Message}'", err.Exception);
            }
        }

        Ensure(
            cancelled.ReplayArn == started.ReplayArn,
            "CancelReplay must name the same replay ARN StartReplay returned");

        var final = await Poll(
            () => events.DescribeReplayAsync(new DescribeReplayRequest { ReplayName = replay }),
            d => d.State?.Value is "CANCELLED" or "COMPLETED" or "FAILED", ReplayTimeoutS,
            "the replay reaches a terminal state");
        Ensure(
            final.State?.Value == "CANCELLED",
            $"a cancelled replay must end CANCELLED, got '{final.State?.Value}' ('{final.StateReason}')");
        Ensure(final.ReplayEndTime is not null, $"a CANCELLED replay must carry ReplayEndTime: {final}");
    }

    private static async Task<T> Poll<T>(Func<Task<T>> read, Func<T, bool> done, double timeoutS, string what)
    {
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

        throw Fail($"timed out after {timeoutS}s waiting until {what}; last read {value}");
    }

    /// <summary>Teardown: a replay still STARTING or RUNNING is cancelled so it does not outlive the run.</summary>
    private static async Task CancelIfLive(IAmazonEventBridge events, string name)
    {
        try
        {
            if ((await events.DescribeReplayAsync(new DescribeReplayRequest { ReplayName = name })).State?.Value is "STARTING" or "RUNNING")
            {
                await events.CancelReplayAsync(new CancelReplayRequest { ReplayName = name });
            }
        }
        catch (Amazon.EventBridge.Model.ResourceNotFoundException)
        {
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
