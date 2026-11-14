// Translated from behavioral/events/check_replay_completion_populates_timestamps_and_list_state_filter_discriminates.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Text.Json.Nodes;
using Amazon.EventBridge.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Events;

/// <summary>events.replay-completion-populates-timestamps-and-list-state-filter-discriminates</summary>
[Category("AwsEvaluationSuite")]
public sealed class ReplayCompletionPopulatesTimestampsAndListStateFilterDiscriminates
{
    /// <summary>
    /// A COMPLETED replay exposes ReplayStartTime, ReplayEndTime and EventLastReplayedTime on
    /// DescribeReplay, and paging ListReplays(State=COMPLETED) finds the replay with every listed
    /// entry COMPLETED.
    ///
    /// Reference: https://docs.aws.amazon.com/eventbridge/latest/APIReference/API_ListReplays.html
    /// </summary>
    [Test]
    [DisplayName("events.replay-completion-populates-timestamps-and-list-state-filter-discriminates")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    [KnownFidelityGap(FidelityGap.EventBridgeArchives, "CreateArchive is not supported (NotSupportedException); AWS archives the event and replays it")]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ev = ctx.Events;
        var bus = FidelityContext.UniqueName("sim2real-behav-bus-");
        var busResp = await ev.CreateEventBusAsync(new CreateEventBusRequest { Name = bus, Tags = [new Tag { Key = "sim2real:managed", Value = "true" }] });
        ctx.OnCleanup(() => ev.DeleteEventBusAsync(new DeleteEventBusRequest { Name = bus }));
        var busArn = busResp.EventBusArn;

        var archiveName = FidelityContext.UniqueName("sim2real-behav-archive-");
        await ev.CreateArchiveAsync(new CreateArchiveRequest { ArchiveName = archiveName, EventSourceArn = busArn });
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

        var beforePublish = FidelityTime.UtcNow.UtcDateTime;
        var markerDetail = $$"""{"marker": "{{FidelityContext.UniqueName("m-")}}"}""";
        var putResp = await ev.PutEventsAsync(new PutEventsRequest
        {
            Entries =
            [
                new PutEventsRequestEntry
                {
                    EventBusName = bus,
                    Source = "sim2real.replay-timestamps",
                    DetailType = "sim2real-marker",
                    Detail = markerDetail,
                },
            ],
        });
        Ensure((putResp.FailedEntryCount ?? 0) == 0, $"marker event rejected: FailedEntryCount={putResp.FailedEntryCount}");

        // Archival is itself async: a replay over a window with nothing archived in it completes
        // instantly with no EventLastReplayedTime, so wait for EventCount to actually move before
        // computing the replay's EventEndTime. EventCount lags well past the archive's own ENABLED
        // transition (50-320 s), so this is the long pole of the check.
        deadline = FidelityTime.StartNew();
        archive = null;
        settled = false;
        while (deadline.Elapsed < TimeSpan.FromSeconds(480))
        {
            archive = await ev.DescribeArchiveAsync(new DescribeArchiveRequest { ArchiveName = archiveName });
            if ((archive.EventCount ?? 0) > 0)
            {
                settled = true;
                break;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(5));
        }

        if (!settled)
        {
            throw Fail($"archive never ingested the marker event within 480s: {Show(archive)}");
        }

        var now = FidelityTime.UtcNow.UtcDateTime;
        var replayName = FidelityContext.UniqueName("sim2real-behav-replay-");
        await ev.StartReplayAsync(new StartReplayRequest
        {
            ReplayName = replayName,
            EventSourceArn = archive!.ArchiveArn,
            EventStartTime = beforePublish - TimeSpan.FromMinutes(1),
            EventEndTime = now,
            Destination = new ReplayDestination { Arn = busArn },
        });

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

        Ensure(desc!.State?.Value == "COMPLETED", $"expected COMPLETED, got '{desc.State?.Value}' ('{desc.StateReason}')");

        foreach (var (field, value) in new[]
        {
            ("ReplayStartTime", desc.ReplayStartTime),
            ("ReplayEndTime", desc.ReplayEndTime),
            ("EventLastReplayedTime", desc.EventLastReplayedTime),
        })
        {
            Ensure(value is not null, $"a COMPLETED replay must populate {field}, got DescribeReplay={Show(desc)}");
        }

        Ensure(
            desc.ReplayEndTime >= desc.ReplayStartTime,
            $"ReplayEndTime {desc.ReplayEndTime:O} must not precede ReplayStartTime {desc.ReplayStartTime:O}");

        // ListReplays rejects State combined with NamePrefix ("At most one filter is allowed"),
        // though the model declares both optional, so find the replay by a bounded page walk.
        var seenNames = new HashSet<string>(StringComparer.Ordinal);
        var seenStates = new HashSet<string>(StringComparer.Ordinal);
        string? token = null;
        for (var i = 0; i < 200; i++)
        {
            var page = await ev.ListReplaysAsync(new ListReplaysRequest
            {
                State = Amazon.EventBridge.ReplayState.COMPLETED,
                Limit = 100,
                NextToken = string.IsNullOrEmpty(token) ? null : token,
            });
            foreach (var r in page.Replays ?? [])
            {
                seenNames.Add(r.ReplayName);
                seenStates.Add(r.State?.Value ?? "");
            }

            if (seenNames.Contains(replayName))
            {
                break;
            }

            token = page.NextToken;
            if (string.IsNullOrEmpty(token))
            {
                break;
            }
        }

        Ensure(
            seenStates.IsSubsetOf(["COMPLETED"]),
            $"every entry ListReplays(State='COMPLETED') returns must itself be COMPLETED, saw states {{{string.Join(", ", seenStates.Select(s => $"'{s}'"))}}}");
        Ensure(
            seenNames.Contains(replayName),
            $"ListReplays(State='COMPLETED') never turned up '{replayName}' across {seenNames.Count} replay(s) paged");
    }

    private static string Show(DescribeArchiveResponse? a) =>
        a is null ? "None" : $"{{'ArchiveName': '{a.ArchiveName}', 'ArchiveArn': '{a.ArchiveArn}', 'State': '{a.State?.Value}', 'EventCount': {a.EventCount}}}";

    private static string Show(DescribeReplayResponse? d) =>
        d is null ? "None" : $"{{'ReplayName': '{d.ReplayName}', 'State': '{d.State?.Value}', 'StateReason': '{d.StateReason}', " +
            $"'ReplayStartTime': '{d.ReplayStartTime:O}', 'ReplayEndTime': '{d.ReplayEndTime:O}', 'EventLastReplayedTime': '{d.EventLastReplayedTime:O}'}}";
}
