// Translated from behavioral/events/check_start_replay_end_time_before_start_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.EventBridge;
using Amazon.EventBridge.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Events;

/// <summary>events.start-replay-end-time-before-start-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class StartReplayEndTimeBeforeStartRejected
{
    /// <summary>
    /// StartReplay with EventEndTime before EventStartTime is rejected with ValidationException and
    /// creates no replay; a valid-window replay on the same archive starts first as a control.
    /// </summary>
    [Test]
    [DisplayName("events.start-replay-end-time-before-start-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ev = ctx.Events;
        var bus = FidelityContext.UniqueName("sim2real-behav-bus-");
        var busResp = await ev.CreateEventBusAsync(new CreateEventBusRequest { Name = bus });
        ctx.OnCleanup(() => ev.DeleteEventBusAsync(new DeleteEventBusRequest { Name = bus }));
        var defaultBusArn = busResp.EventBusArn;

        var archiveName = FidelityContext.UniqueName("sim2real-behav-archive-");
        await ev.CreateArchiveAsync(new CreateArchiveRequest { ArchiveName = archiveName, EventSourceArn = defaultBusArn });
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
        var earlier = now - TimeSpan.FromHours(1);

        // Positive control: the same call shape, a valid (start < end) window, on the same archive.
        var controlName = FidelityContext.UniqueName("sim2real-behav-replay-ctl-");
        var controlResp = await ev.StartReplayAsync(new StartReplayRequest
        {
            ReplayName = controlName,
            EventSourceArn = desc.ArchiveArn,
            EventStartTime = earlier,
            EventEndTime = now,
            Destination = new ReplayDestination { Arn = defaultBusArn },
        });
        ctx.OnCleanup(() => BestEffortCancel(ev, controlName));
        Ensure(
            controlResp.State?.Value is "STARTING" or "RUNNING" or "COMPLETED",
            $"the positive control (a valid time window) did not start, got {{'State': '{controlResp.State?.Value}'}}");

        var replayName = FidelityContext.UniqueName("sim2real-behav-replay-");

        var error = await ClientError(() => ev.StartReplayAsync(new StartReplayRequest
        {
            ReplayName = replayName,
            EventSourceArn = desc.ArchiveArn,
            EventStartTime = now,       // start AFTER end -> invalid range
            EventEndTime = earlier,
            Destination = new ReplayDestination { Arn = defaultBusArn },
        }));
        Ensure(
            error.Code == "ValidationException",
            $"StartReplay with EventEndTime before EventStartTime was rejected with '{error.Code}', expected \"ValidationException\"");
        const string expectedMessage = "Parameter EventEndTime is not valid. Reason: EventStartTime must be before EventEndTime.";
        Ensure(error.Message == expectedMessage, $"expected message '{expectedMessage}', got '{error.Message}'");

        var listResp = (await ev.ListReplaysAsync(new ListReplaysRequest { NamePrefix = replayName })).Replays
            ?? throw new KeyNotFoundException("'Replays'");
        Ensure(
            listResp.Count == 0,
            $"an invalid StartReplay call must not create a Replay resource: [{string.Join(", ", listResp.Select(r => $"'{r.ReplayName}'"))}]");
    }

    /// <summary>Call fn() and return its botocore Error dict; an unexpected success raises.</summary>
    private static async Task<AwsError> ClientError<T>(Func<Task<T>> fn)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        return error ?? throw Fail($"real AWS rejects this call, but it SUCCEEDED and returned {result}");
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
