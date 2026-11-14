// Translated from behavioral/events/check_describe_replay_nonexistent_not_found.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.EventBridge;
using Amazon.EventBridge.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Events;

/// <summary>events.describe-replay-nonexistent-not-found</summary>
[Category("AwsEvaluationSuite")]
public sealed class DescribeReplayNonexistentNotFound
{
    private static readonly string[] LiveStates = ["STARTING", "RUNNING", "COMPLETED"];

    /// <summary>
    /// DescribeReplay for a never-started replay name raises ResourceNotFoundException and
    /// ListReplays omits it; a control replay succeeds, and starting a replay under the probed
    /// name afterwards makes the identical DescribeReplay call succeed.
    /// </summary>
    [Test]
    [DisplayName("events.describe-replay-nonexistent-not-found")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    [KnownFidelityGap(FidelityGap.EventBridgeArchives, "CreateArchive is not supported (NotSupportedException)")]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ev = ctx.Events;

        var busName = FidelityContext.UniqueName("sim2real-behav-bus-");
        var busArn = (await ev.CreateEventBusAsync(new CreateEventBusRequest { Name = busName })).EventBusArn;
        ctx.OnCleanup(() => ev.DeleteEventBusAsync(new DeleteEventBusRequest { Name = busName }));

        var archiveName = FidelityContext.UniqueName("sim2real-behav-archive-");
        var archiveArn = (await ev.CreateArchiveAsync(new CreateArchiveRequest { ArchiveName = archiveName, EventSourceArn = busArn })).ArchiveArn;
        ctx.OnCleanup(() => ev.DeleteArchiveAsync(new DeleteArchiveRequest { ArchiveName = archiveName }));
        await PollArchiveSettled(ev, archiveName);

        var now = FidelityTime.UtcNow.UtcDateTime;
        var windowStart = now - TimeSpan.FromMinutes(10);
        var windowEnd = now;

        // Positive control: DescribeReplay on a real, just-started replay must succeed -- without
        // this, the not-found assertion below cannot tell "correctly reports absence" from
        // "DescribeReplay rejects every call regardless of the name".
        var controlName = FidelityContext.UniqueName("sim2real-behav-replay-ctl-");
        await ev.StartReplayAsync(new StartReplayRequest
        {
            ReplayName = controlName,
            EventSourceArn = archiveArn,
            EventStartTime = windowStart,
            EventEndTime = windowEnd,
            Destination = new ReplayDestination { Arn = busArn },
        });
        ctx.OnCleanup(() => BestEffortCancel(ev, controlName));
        var controlDesc = await PollReplayState(ev, controlName, LiveStates);
        Ensure(
            LiveStates.Contains(controlDesc.State?.Value),
            "control replay against a real archive did not reach a live state, so the refusal below " +
            $"would be indistinguishable from DescribeReplay rejecting everything: {controlDesc}");

        // The probe: a name that was never passed to StartReplay.
        var probeName = FidelityContext.UniqueName("sim2real-behav-replay-");
        var err = await Error(() => ev.DescribeReplayAsync(new DescribeReplayRequest { ReplayName = probeName }));
        Ensure(
            err == ("ResourceNotFoundException", $"Replay {probeName} does not exist."),
            $"expected ResourceNotFoundException, got ('{err.Code}', '{err.Message}')");

        // Effect absent via an independent read: the probed name never shows up in ListReplays either.
        var listed = ((await ev.ListReplaysAsync(new ListReplaysRequest { NamePrefix = probeName })).Replays ?? [])
            .Select(r => r.ReplayName).ToHashSet(StringComparer.Ordinal);
        Ensure(
            !listed.Contains(probeName),
            $"describe_replay reported '{probeName}' not-found but list_replays shows it: {{{string.Join(", ", listed.Select(n => $"'{n}'"))}}}");

        // Restore: start a replay under the EXACT probed name. The identical DescribeReplay call
        // must now succeed, proving the earlier refusal was specifically about that name never having
        // existed, not some other rejection.
        await ev.StartReplayAsync(new StartReplayRequest
        {
            ReplayName = probeName,
            EventSourceArn = archiveArn,
            EventStartTime = windowStart,
            EventEndTime = windowEnd,
            Destination = new ReplayDestination { Arn = busArn },
        });
        ctx.OnCleanup(() => BestEffortCancel(ev, probeName));
        var restoredDesc = await PollReplayState(ev, probeName, LiveStates);
        Ensure(
            LiveStates.Contains(restoredDesc.State?.Value),
            $"identical DescribeReplay(ReplayName='{probeName}') should succeed once that name is " +
            $"actually started, got {restoredDesc}");
    }

    /// <summary>(code, message) of a call that must fail; a call that succeeds raises AssertionError.</summary>
    private static async Task<(string? Code, string? Message)> Error<T>(Func<Task<T>> fn)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is null)
        {
            throw Fail($"expected a ClientError, but the call succeeded and returned {result}");
        }

        return (error.Code, error.Message);
    }

    private static async Task<DescribeArchiveResponse> PollArchiveSettled(IAmazonEventBridge ev, string archiveName, double timeoutS = 60.0)
    {
        var deadline = FidelityTime.StartNew();
        DescribeArchiveResponse? desc = null;
        while (deadline.Elapsed < TimeSpan.FromSeconds(timeoutS))
        {
            desc = await ev.DescribeArchiveAsync(new DescribeArchiveRequest { ArchiveName = archiveName });
            if (desc.State?.Value is "ENABLED" or "CREATE_FAILED")
            {
                if (desc.State.Value != "ENABLED")
                {
                    throw Fail($"archive {archiveName} failed to create: {desc}");
                }

                return desc;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(1));
        }

        throw Fail($"archive {archiveName} did not settle within {timeoutS:0.0}s: {desc}");
    }

    private static async Task<DescribeReplayResponse> PollReplayState(IAmazonEventBridge ev, string replayName, string[] terminalStates, double timeoutS = 30.0)
    {
        var deadline = FidelityTime.StartNew();
        DescribeReplayResponse? desc = null;
        while (deadline.Elapsed < TimeSpan.FromSeconds(timeoutS))
        {
            desc = await ev.DescribeReplayAsync(new DescribeReplayRequest { ReplayName = replayName });
            var state = desc.State?.Value;
            if (terminalStates.Contains(state) || state is "FAILED" or "CANCELLED")
            {
                return desc;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(2));
        }

        throw Fail($"replay {replayName} did not reach a live state within {timeoutS:0.0}s: {desc}");
    }

    private static async Task BestEffortCancel(IAmazonEventBridge ev, string replayName)
    {
        try
        {
            var state = (await ev.DescribeReplayAsync(new DescribeReplayRequest { ReplayName = replayName })).State?.Value;
            if (state is "STARTING" or "RUNNING")
            {
                await ev.CancelReplayAsync(new CancelReplayRequest { ReplayName = replayName });
            }
        }
        catch (Amazon.EventBridge.Model.ResourceNotFoundException)
        {
        }
    }
}
