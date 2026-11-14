// Translated from behavioral/events/check_replay_reaches_completed.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.EventBridge.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Events;

/// <summary>events.replay-reaches-completed</summary>
[Category("AwsEvaluationSuite")]
public sealed class ReplayReachesCompleted
{
    /// <summary>
    /// StartReplay over an empty time window transitions the replay through STARTING/RUNNING to COMPLETED, and CancelReplay on a COMPLETED replay is rejected with IllegalStatusException (terminal state is immutable).
    /// </summary>
    [Test]
    [DisplayName("events.replay-reaches-completed")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ev = ctx.Events;
        var bus = FidelityContext.UniqueName("sim2real-behav-bus-");
        var busResp = await ev.CreateEventBusAsync(new CreateEventBusRequest { Name = bus, Tags = [new Tag { Key = "sim2real:managed", Value = "true" }] });
        ctx.OnCleanup(() => ev.DeleteEventBusAsync(new DeleteEventBusRequest { Name = bus }));
        var defaultBusArn = busResp.EventBusArn;

        var archiveName = FidelityContext.UniqueName("sim2real-behav-archive-");
        await ev.CreateArchiveAsync(new CreateArchiveRequest { ArchiveName = archiveName, EventSourceArn = defaultBusArn });
        ctx.OnCleanup(() => ev.DeleteArchiveAsync(new DeleteArchiveRequest { ArchiveName = archiveName }));

        var deadline = FidelityTime.StartNew();
        DescribeArchiveResponse? archive = null;
        var settled = false;
        while (deadline.Elapsed < TimeSpan.FromSeconds(60))
        {
            archive = await ev.DescribeArchiveAsync(new DescribeArchiveRequest { ArchiveName = archiveName });
            if (archive.State?.Value is "ENABLED" or "CREATE_FAILED")
            {
                settled = true;
                break;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(1));
        }

        if (!settled)
        {
            throw Fail($"archive did not settle within 60s: {Show(archive)}");
        }

        Ensure(archive!.State?.Value == "ENABLED", $"archive did not enable: {Show(archive)}");

        var now = FidelityTime.UtcNow.UtcDateTime;
        var replayName = FidelityContext.UniqueName("sim2real-behav-replay-");
        var start = await ev.StartReplayAsync(new StartReplayRequest
        {
            ReplayName = replayName,
            EventSourceArn = archive.ArchiveArn,
            EventStartTime = now - TimeSpan.FromMinutes(10),
            EventEndTime = now,
            Destination = new ReplayDestination { Arn = defaultBusArn },
        });
        Ensure(start.State?.Value is "STARTING" or "RUNNING" or "COMPLETED", $"unexpected initial state: {{'State': '{start.State?.Value}', 'ReplayArn': '{start.ReplayArn}'}}");
        Ensure(start.ReplayArn.EndsWith(replayName, StringComparison.Ordinal), "assert start[\"ReplayArn\"].endswith(replay_name)");

        deadline = FidelityTime.StartNew();
        DescribeReplayResponse? desc = null;
        settled = false;
        while (deadline.Elapsed < TimeSpan.FromSeconds(300))
        {
            desc = await ev.DescribeReplayAsync(new DescribeReplayRequest { ReplayName = replayName });
            if (desc.State?.Value is "COMPLETED" or "FAILED" or "CANCELLED")
            {
                settled = true;
                break;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(5));
        }

        if (!settled)
        {
            throw Fail($"replay did not settle within 300s: {Show(desc)}");
        }

        Ensure(desc!.State?.Value == "COMPLETED", $"expected COMPLETED, got {desc.State?.Value} ({desc.StateReason})");
        Ensure(desc.EventSourceArn == archive.ArchiveArn, "assert desc[\"EventSourceArn\"] == archive[\"ArchiveArn\"]");
        Ensure(desc.ReplayEndTime is not null, $"COMPLETED replay must expose ReplayEndTime: {Show(desc)}");

        var listed = ((await ev.ListReplaysAsync(new ListReplaysRequest { NamePrefix = replayName })).Replays
            ?? throw new KeyNotFoundException("'Replays'")).Select(r => r.ReplayName).ToHashSet();
        Ensure(listed.Contains(replayName), $"completed replay must still be listable, got {{{string.Join(", ", listed.Select(n => $"'{n}'"))}}}");

        var err = await ClientError(() => ev.CancelReplayAsync(new CancelReplayRequest { ReplayName = replayName }));
        Ensure(err.Code == "IllegalStatusException", $"expected IllegalStatusException cancelling a COMPLETED replay, got {err}");
        var expectedMessage = $"Replay {replayName} is not in a valid state for this operation.";
        Ensure(err.Message == expectedMessage, $"expected message '{expectedMessage}', got '{err.Message}'");
    }

    private static async Task<AwsError> ClientError<T>(Func<Task<T>> fn)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        return error ?? throw Fail($"expected this call to be rejected, but it SUCCEEDED and returned {result}");
    }

    private static string Show(DescribeArchiveResponse? a) =>
        a is null ? "None" : $"{{'ArchiveName': '{a.ArchiveName}', 'ArchiveArn': '{a.ArchiveArn}', 'State': '{a.State?.Value}', 'StateReason': '{a.StateReason}'}}";

    private static string Show(DescribeReplayResponse? d) =>
        d is null ? "None" : $"{{'ReplayName': '{d.ReplayName}', 'State': '{d.State?.Value}', 'StateReason': '{d.StateReason}', 'ReplayEndTime': '{d.ReplayEndTime:O}'}}";
}
