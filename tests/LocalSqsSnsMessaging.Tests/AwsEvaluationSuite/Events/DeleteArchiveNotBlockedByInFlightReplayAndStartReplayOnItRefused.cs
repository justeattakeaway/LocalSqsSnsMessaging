// Translated from behavioral/events/check_delete_archive_not_blocked_by_in_flight_replay_and_start_replay_on_it_refused.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.EventBridge;
using Amazon.EventBridge.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Events;

/// <summary>events.delete-archive-not-blocked-by-in-flight-replay-and-start-replay-on-it-refused</summary>
[Category("AwsEvaluationSuite")]
public sealed class DeleteArchiveNotBlockedByInFlightReplayAndStartReplayOnItRefused
{
    private const string Check = "delete-archive-not-blocked-by-in-flight-replay-and-start-replay-on-it-refused";
    private const double ArchiveSettleTimeoutS = 90.0;

    /// <summary>
    /// DeleteArchive succeeds while a replay from it is in flight (no dependency refusal), and a
    /// later StartReplay naming the deleted archive is a ValidationException naming the archive.
    /// </summary>
    [Test]
    [DisplayName("events.delete-archive-not-blocked-by-in-flight-replay-and-start-replay-on-it-refused")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    [KnownFidelityGap(FidelityGap.EventBridgeArchives, "CreateArchive is not supported (NotSupportedException)")]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var events = ctx.Events;
        var bus = FidelityContext.UniqueName("sim2real-behav-bus-");
        var busArn = (await events.CreateEventBusAsync(new CreateEventBusRequest { Name = bus, Tags = [new Tag { Key = "sim2real", Value = Check }] })).EventBusArn;
        ctx.OnCleanup(() => events.DeleteEventBusAsync(new DeleteEventBusRequest { Name = bus }));

        var archive = FidelityContext.UniqueName("sim2real-behav-arch-");
        var archiveArn = (await events.CreateArchiveAsync(new CreateArchiveRequest { ArchiveName = archive, EventSourceArn = busArn })).ArchiveArn;
        ctx.OnCleanup(() => DeleteArchiveQuietly(events, archive));
        var state = await Poll(
            async () => (await events.DescribeArchiveAsync(new DescribeArchiveRequest { ArchiveName = archive })).State?.Value,
            s => s is "ENABLED" or "CREATE_FAILED",
            ArchiveSettleTimeoutS,
            "archive settles");
        Ensure(state == "ENABLED", $"archive must be ENABLED before replaying, got '{state}'");

        var now = FidelityTime.UtcNow.UtcDateTime;
        var replay = FidelityContext.UniqueName("sim2real-behav-rp-");
        var started = await events.StartReplayAsync(new StartReplayRequest
        {
            ReplayName = replay,
            EventSourceArn = archiveArn,
            Destination = new ReplayDestination { Arn = busArn },
            EventStartTime = now - TimeSpan.FromHours(1),
            EventEndTime = now,
        });
        ctx.OnCleanup(() => CancelIfLive(events, replay));
        Ensure(
            started.State?.Value is "STARTING" or "RUNNING",
            $"StartReplay must answer with a live, non-terminal state, got '{started.State?.Value}'");

        // Delete immediately after StartReplay's ack, while the replay is still reading the archive.
        await events.DeleteArchiveAsync(new DeleteArchiveRequest { ArchiveName = archive });
        var archiveGone = await Poll(
            () => IsNotFound(() => events.DescribeArchiveAsync(new DescribeArchiveRequest { ArchiveName = archive })),
            gone => gone,
            ArchiveSettleTimeoutS,
            "archive disappears after DeleteArchive");
        Ensure(
            archiveGone,
            $"DeleteArchive({archive}) while its replay was in flight must still make the archive " +
            "disappear (DescribeArchive never reached ResourceNotFoundException)");

        // A fresh replay name keeps the refusal about the archive, not about a name already in use.
        var retry = FidelityContext.UniqueName("sim2real-behav-rp-");
        ctx.OnCleanup(() => CancelIfLive(events, retry));
        var (code, message) = await Error(() => events.StartReplayAsync(new StartReplayRequest
        {
            ReplayName = retry,
            EventSourceArn = archiveArn,
            Destination = new ReplayDestination { Arn = busArn },
            EventStartTime = now - TimeSpan.FromHours(1),
            EventEndTime = now,
        }));
        Ensure(
            code == "ValidationException",
            "StartReplay naming the now-deleted archive must be refused with ValidationException, " +
            $"got '{code}' ('{message}')");
        var expectedMessage = $"Parameter EventSourceArn is not valid. Reason: Archive {archive} does not exist.";
        Ensure(
            message == expectedMessage,
            $"expected the refusal message '{expectedMessage}', got '{message}'");
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

            await FidelityTime.Delay(TimeSpan.FromSeconds(3));
        }

        throw Fail($"timed out after {timeoutS:0.0}s waiting until {what}; last read {value}");
    }

    /// <summary>Cancel a replay still STARTING or RUNNING so it does not outlive the run.</summary>
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

    private static async Task DeleteArchiveQuietly(IAmazonEventBridge events, string name)
    {
        try
        {
            await events.DeleteArchiveAsync(new DeleteArchiveRequest { ArchiveName = name });
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

    /// <summary>
    /// A poll probe: True once fn() raises ResourceNotFoundException, False while it still
    /// succeeds or raises anything else.
    /// </summary>
    private static async Task<bool> IsNotFound(Func<Task> fn) =>
        (await FidelityContext.CaptureError(fn))?.Code == "ResourceNotFoundException";
}
