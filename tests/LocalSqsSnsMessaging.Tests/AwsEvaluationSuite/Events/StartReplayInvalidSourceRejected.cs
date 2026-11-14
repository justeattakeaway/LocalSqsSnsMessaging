// Translated from behavioral/events/check_start_replay_invalid_source_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.EventBridge;
using Amazon.EventBridge.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Events;

/// <summary>events.start-replay-invalid-source-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class StartReplayInvalidSourceRejected
{
    /// <summary>
    /// StartReplay referencing an archive ARN that does not exist is rejected: real EventBridge validates the source ARN and returns ValidationException rather than starting a replay that silently produces nothing. A positive control on a genuinely existing archive proves the account and credentials are otherwise healthy for StartReplay, so the rejection is specific to the bogus source and not a blanket refusal.
    /// </summary>
    [Test]
    [DisplayName("events.start-replay-invalid-source-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
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

        var deadline = FidelityTime.StartNew();
        DescribeArchiveResponse? desc = null;
        var settled = false;
        while (deadline.Elapsed < TimeSpan.FromSeconds(60))
        {
            desc = await ev.DescribeArchiveAsync(new DescribeArchiveRequest { ArchiveName = archiveName });
            if (desc.State?.Value is "ENABLED" or "CREATE_FAILED")
            {
                settled = true;
                break;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(1));
        }

        if (!settled)
        {
            throw Fail($"archive did not settle within 60s: {Show(desc)}");
        }

        Ensure(desc!.State?.Value == "ENABLED", $"archive did not enable: {Show(desc)}");

        var now = FidelityTime.UtcNow.UtcDateTime;

        // Positive control: StartReplay against the real (existing) archive succeeds, so the
        // rejection below can be attributed to the bogus source, not a blanket refusal.
        var controlReplayName = FidelityContext.UniqueName("sim2real-behav-replay-ctrl-");
        var control = await ev.StartReplayAsync(new StartReplayRequest
        {
            ReplayName = controlReplayName,
            EventSourceArn = archiveArn,
            EventStartTime = now - TimeSpan.FromHours(1),
            EventEndTime = now,
            Destination = new ReplayDestination { Arn = busArn },
        });
        Ensure(
            control.State?.Value is "STARTING" or "RUNNING" or "COMPLETED",
            $"StartReplay against a real archive must succeed, got {control.State?.Value}");
        ctx.OnCleanup(() => BestEffortCancel(ev, controlReplayName));

        // Break exactly one thing: an archive ARN that does not exist.
        var bogusArchiveArn = archiveArn + "-does-not-exist";
        var replayName = FidelityContext.UniqueName("sim2real-behav-replay-");

        var err = await ClientError(() => ev.StartReplayAsync(new StartReplayRequest
        {
            ReplayName = replayName,
            EventSourceArn = bogusArchiveArn,
            EventStartTime = now - TimeSpan.FromHours(1),
            EventEndTime = now,
            Destination = new ReplayDestination { Arn = busArn },
        }));
        Ensure(err.Code == "ValidationException", $"expected ValidationException, got {err}");
        var expectedMessage = $"Parameter EventSourceArn is not valid. Reason: Archive {archiveName}-does-not-exist does not exist.";
        Ensure(err.Message == expectedMessage, $"expected message '{expectedMessage}', got '{err.Message}'");

        var listed = (await ev.ListReplaysAsync(new ListReplaysRequest { NamePrefix = replayName })).Replays
            ?? throw new KeyNotFoundException("'Replays'");
        Ensure(listed.Count == 0, $"rejected StartReplay must not create a replay: [{string.Join(", ", listed.Select(r => $"'{r.ReplayName}'"))}]");
    }

    /// <summary>
    /// Call fn(); return its botocore Error dict. An unexpected SUCCESS raises, because an emulator
    /// ACCEPTING input real AWS refuses is the strongest signal available.
    /// </summary>
    private static async Task<AwsError> ClientError<T>(Func<Task<T>> fn)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        return error ?? throw Fail($"expected rejection, but the call SUCCEEDED and returned {result}");
    }

    private static async Task BestEffortCancel(AmazonEventBridgeClient ev, string replayName)
    {
        var error = await FidelityContext.CaptureError(async () =>
        {
            var state = (await ev.DescribeReplayAsync(new DescribeReplayRequest { ReplayName = replayName })).State?.Value;
            if (state is "STARTING" or "RUNNING")
            {
                await ev.CancelReplayAsync(new CancelReplayRequest { ReplayName = replayName });
            }
        });
        if (error is not null && error.Code != "ResourceNotFoundException")
        {
            throw error.Exception;
        }
    }

    private static string Show(DescribeArchiveResponse? a) =>
        a is null ? "None" : $"{{'ArchiveName': '{a.ArchiveName}', 'ArchiveArn': '{a.ArchiveArn}', 'State': '{a.State?.Value}', 'StateReason': '{a.StateReason}'}}";
}
