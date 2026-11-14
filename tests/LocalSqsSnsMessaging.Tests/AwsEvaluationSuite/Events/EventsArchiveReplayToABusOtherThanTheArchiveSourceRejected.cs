// Translated from behavioral/events/check_events_archive_replay_to_a_bus_other_than_the_archive_source_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.EventBridge;
using Amazon.EventBridge.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Events;

/// <summary>events.events-archive-replay-to-a-bus-other-than-the-archive-source-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class EventsArchiveReplayToABusOtherThanTheArchiveSourceRejected
{
    /// <summary>
    /// StartReplay into a bus other than the archive's source is refused with ValidationException
    /// and creates no replay; the same request aimed at the archive's own source bus is accepted.
    /// </summary>
    /// <remarks>
    /// Reference: https://docs.aws.amazon.com/eventbridge/latest/APIReference/API_StartReplay.html
    /// </remarks>
    [Test]
    [DisplayName("events.events-archive-replay-to-a-bus-other-than-the-archive-source-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    [KnownFidelityGap(FidelityGap.EventBridgeArchives, "CreateArchive is not supported (NotSupportedException)")]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var events = ctx.Events;

        var sourceBus = FidelityContext.UniqueName("sim2real-arr-src-");
        var sourceArn = (await events.CreateEventBusAsync(new CreateEventBusRequest { Name = sourceBus, Tags = TagsKv() })).EventBusArn;
        ctx.OnCleanup(() => events.DeleteEventBusAsync(new DeleteEventBusRequest { Name = sourceBus }));
        var otherBus = FidelityContext.UniqueName("sim2real-arr-oth-");
        var otherArn = (await events.CreateEventBusAsync(new CreateEventBusRequest { Name = otherBus, Tags = TagsKv() })).EventBusArn;
        ctx.OnCleanup(() => events.DeleteEventBusAsync(new DeleteEventBusRequest { Name = otherBus }));

        var archive = FidelityContext.UniqueName("sim2real-arr-arc-");
        var archiveArn = (await events.CreateArchiveAsync(new CreateArchiveRequest { ArchiveName = archive, EventSourceArn = sourceArn })).ArchiveArn;
        ctx.OnCleanup(() => DeleteArchiveAndWait(events, archive));
        await WaitArchiveEnabled(events, archive);

        var now = FidelityTime.UtcNow.UtcDateTime;
        var windowStart = now - TimeSpan.FromMinutes(10);
        var windowEnd = now - TimeSpan.FromMinutes(1);

        var refused = FidelityContext.UniqueName("sim2real-arr-rp-bad-");
        var err = await Error(() => events.StartReplayAsync(new StartReplayRequest
        {
            ReplayName = refused, EventSourceArn = archiveArn, Destination = new ReplayDestination { Arn = otherArn },
            EventStartTime = windowStart, EventEndTime = windowEnd,
        }));
        // The Smithy model does not list ValidationException for StartReplay, but AWS returns it.
        var expected = (
            "ValidationException",
            "Parameter Destination.Arn is not valid. Reason: Cross event bus replay is not permitted.");
        Ensure(
            err == expected,
            $"StartReplay from an archive of '{sourceBus}' into '{otherBus}' returned {Repr(err)}, " +
            $"expected {Repr(expected)}");
        var absent = await Error(() => events.DescribeReplayAsync(new DescribeReplayRequest { ReplayName = refused }));
        Ensure(
            absent == ("ResourceNotFoundException", $"Replay {refused} does not exist."),
            $"the refused replay '{refused}' is describable afterwards (DescribeReplay -> {Repr(absent)}), " +
            "so the refusal still created it");
        var listed = await ListReplayNames(events, refused);
        Ensure(listed.Count == 0, $"the refused replay '{refused}' is listed: [{string.Join(", ", listed.Select(n => $"'{n}'"))}]");

        // Positive control: the same request, differing only in the destination bus, is accepted.
        var accepted = FidelityContext.UniqueName("sim2real-arr-rp-ok-");
        // No DeleteReplay API exists, so the accepted replay cannot be cleaned up.
        var ok = await events.StartReplayAsync(new StartReplayRequest
        {
            ReplayName = accepted, EventSourceArn = archiveArn, Destination = new ReplayDestination { Arn = sourceArn },
            EventStartTime = windowStart, EventEndTime = windowEnd,
        });
        Ensure((ok.ReplayArn ?? "").EndsWith(accepted, StringComparison.Ordinal), $"control StartReplay returned {ok}");
        var desc = await events.DescribeReplayAsync(new DescribeReplayRequest { ReplayName = accepted });
        Ensure(
            desc.Destination?.Arn == sourceArn,
            $"control replay's Destination is {desc.Destination}, expected '{sourceArn}'");
        Ensure(
            desc.EventSourceArn == archiveArn,
            $"control replay's EventSourceArn is '{desc.EventSourceArn}', expected '{archiveArn}'");
    }

    private static List<Tag> TagsKv() => [new Tag { Key = "sim2real:managed", Value = "true" }];

    private static string Repr((string? Code, string? Message) e) => $"('{e.Code}', '{e.Message}')";

    /// <summary>A call expected to fail that succeeds raises (the Python's <c>_Accepted</c>), so it can never read as an error code.</summary>
    private static async Task<(string? Code, string? Message)> Error<T>(Func<Task<T>> fn)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is null)
        {
            throw new InvalidOperationException($"call was expected to be refused but succeeded: {result}");
        }

        return (error.Code ?? "<no code>", error.Message);
    }

    private static async Task<List<string>> ListReplayNames(IAmazonEventBridge events, string prefix)
    {
        var names = new List<string>();
        string? token = null;
        while (true)
        {
            var page = await events.ListReplaysAsync(new ListReplaysRequest { NamePrefix = prefix, NextToken = token });
            names.AddRange((page.Replays ?? []).Select(r => r.ReplayName));
            if (string.IsNullOrEmpty(page.NextToken))
            {
                return names;
            }

            token = page.NextToken;
        }
    }

    private static async Task WaitArchiveEnabled(IAmazonEventBridge events, string name, double timeoutS = 300.0)
    {
        var deadline = FidelityTime.StartNew();
        string? state = null;
        while (deadline.Elapsed < TimeSpan.FromSeconds(timeoutS))
        {
            state = (await events.DescribeArchiveAsync(new DescribeArchiveRequest { ArchiveName = name })).State?.Value;
            if (state is "ENABLED" or "CREATE_FAILED" or "DISABLED")
            {
                break;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(3));
        }

        Ensure(state == "ENABLED", $"archive {name} settled at State='{state}', expected 'ENABLED'");
    }

    /// <summary>The bus cannot be deleted while an archive still references it, so wait for the delete.</summary>
    private static async Task DeleteArchiveAndWait(IAmazonEventBridge events, string name, double timeoutS = 180.0)
    {
        var error = await FidelityContext.CaptureError(() => events.DeleteArchiveAsync(new DeleteArchiveRequest { ArchiveName = name }));
        if (error is not null && error.Code != "ResourceNotFoundException")
        {
            throw new InvalidOperationException($"could not delete archive {name}: {error}", error.Exception);
        }

        var deadline = FidelityTime.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(timeoutS))
        {
            var describeError = await FidelityContext.CaptureError(() => events.DescribeArchiveAsync(new DescribeArchiveRequest { ArchiveName = name }));
            if (describeError?.Code == "ResourceNotFoundException")
            {
                return;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(3));
        }

        throw new InvalidOperationException($"archive {name} still present {timeoutS:0.0}s after DeleteArchive");
    }
}
