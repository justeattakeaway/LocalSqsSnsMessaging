// Translated from behavioral/events/check_update_archive_event_pattern_governs_what_is_archived_afterwards.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Text.Json.Nodes;
using Amazon.EventBridge;
using Amazon.EventBridge.Model;
using Amazon.Runtime;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;
using EventBridgeTag = Amazon.EventBridge.Model.Tag;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Events;

/// <summary>events.update-archive-event-pattern-governs-what-is-archived-afterwards</summary>
[Category("AwsEvaluationSuite")]
public sealed class UpdateArchiveEventPatternGovernsWhatIsArchivedAfterwards
{
    private const string Check = "update-archive-event-pattern-governs-what-is-archived-afterwards";
    private const double RuleConvergeTimeoutS = 60.0;
    private const double ArchiveDeleteTimeoutS = 180.0;
    // Archive EventCount lags PutEvents by minutes (151 s to beyond 900 s observed).
    private const double IngestTimeoutS = 1500.0;

    /// <summary>
    /// After UpdateArchive replaces an archive's EventPattern, the archive stores exactly the 2
    /// events the new pattern matches, not the 1 only the old pattern matched (no filtering gives 3).
    /// </summary>
    [Test]
    [DisplayName("events.update-archive-event-pattern-governs-what-is-archived-afterwards")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    [KnownFidelityGap(FidelityGap.EventBridgeArchives, "CreateArchive is not implemented: it fails with NotSupportedException \"EventBridge operation 'CreateArchive' is not supported.\"")]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var events = ctx.Events;

        var bus = FidelityContext.UniqueName("sim2real-behav-bus-");
        var busArn = (await events.CreateEventBusAsync(new CreateEventBusRequest
        {
            Name = bus, Tags = [new EventBridgeTag { Key = "sim2real", Value = Check }],
        })).EventBusArn;
        ctx.OnCleanup(() => events.DeleteEventBusAsync(new DeleteEventBusRequest { Name = bus }));

        var oldSource = FidelityContext.UniqueName("sim2real.old.");
        var newSource = FidelityContext.UniqueName("sim2real.new.");

        var filtered = FidelityContext.UniqueName("sim2real-arc-");
        await events.CreateArchiveAsync(new CreateArchiveRequest
        {
            ArchiveName = filtered, EventSourceArn = busArn,
            EventPattern = $$"""{"source": ["{{oldSource}}"]}""",
        });
        ctx.OnCleanup(() => DeleteArchiveAndWait(events, filtered));
        var unfiltered = FidelityContext.UniqueName("sim2real-arc-");
        await events.CreateArchiveAsync(new CreateArchiveRequest { ArchiveName = unfiltered, EventSourceArn = busArn });
        ctx.OnCleanup(() => DeleteArchiveAndWait(events, unfiltered));

        foreach (var name in new[] { filtered, unfiltered })
        {
            var state = await PollUntil(
                () => events.DescribeArchiveAsync(new DescribeArchiveRequest { ArchiveName = name }),
                d => d.State?.Value is "ENABLED" or "CREATE_FAILED" or "DISABLED",
                timeoutS: 300.0, intervalS: 3.0, description: $"archive {name} leaving CREATING");
            Ensure(state.State?.Value == "ENABLED", $"archive {name} settled at {Repr(state)}");
            Ensure(
                state.EventCount == 0,
                $"archive {name} counted {Repr(state.EventCount)} events before any was published");
        }

        await events.UpdateArchiveAsync(new UpdateArchiveRequest { ArchiveName = filtered, EventPattern = $$"""{"source": ["{{newSource}}"]}""" });
        // The archive's managed rule executes its selection; wait for it so the put cannot race it.
        await PollRulePattern(
            events, bus, $"Events-Archive-{filtered}",
            new JsonObject
            {
                ["source"] = new JsonArray(newSource),
                ["replay-name"] = new JsonArray(new JsonObject { ["exists"] = false }),
            });

        var put = await events.PutEventsAsync(new PutEventsRequest
        {
            Entries =
            [
                new PutEventsRequestEntry { EventBusName = bus, Source = oldSource, DetailType = "sim2real-archived", Detail = """{"i": 1}""" },
                new PutEventsRequestEntry { EventBusName = bus, Source = newSource, DetailType = "sim2real-archived", Detail = """{"i": 2}""" },
                new PutEventsRequestEntry { EventBusName = bus, Source = newSource, DetailType = "sim2real-archived", Detail = """{"i": 3}""" },
            ],
        });
        Ensure(
            put.FailedEntryCount == 0 && (put.Entries ?? []).All(e => !string.IsNullOrEmpty(e.EventId)),
            $"PutEvents must accept all three entries, got [{string.Join(", ", (put.Entries ?? []).Select(e => $"{e.EventId} {e.ErrorCode}"))}]");

        // Barrier: the unfiltered archive reaching 3 shows all three events have been processed.
        var barrier = await PollUntil(
            () => events.DescribeArchiveAsync(new DescribeArchiveRequest { ArchiveName = unfiltered }),
            d => (d.EventCount ?? 0) >= 3,
            timeoutS: IngestTimeoutS, intervalS: 10.0,
            description: $"unfiltered archive {unfiltered} accounting for all 3 published events");
        Ensure(
            barrier.EventCount == 3,
            $"the unfiltered archive stored {Repr(barrier.EventCount)} events for 3 published");

        await PollUntil(
            () => events.DescribeArchiveAsync(new DescribeArchiveRequest { ArchiveName = filtered }),
            d => (d.EventCount ?? 0) >= 2,
            timeoutS: 900.0, intervalS: 10.0,
            description:
                $"updated archive {filtered} accounting for the 2 events its NEW pattern matches " +
                "(an archive still filtering on the old pattern stops at 1)");
        // The two archives' counts are not updated in a fixed order, so hold the count stable.
        var settled = await WaitUntilStable(
            () => events.DescribeArchiveAsync(new DescribeArchiveRequest { ArchiveName = filtered }),
            d => d.EventCount,
            windowS: 20.0, intervalS: 10.0, description: $"archive {filtered} EventCount");
        Ensure(
            settled.EventCount == 2,
            $"after UpdateArchive to source '{newSource}', the archive settled at EventCount=" +
            $"{Repr(settled.EventCount)}; expected exactly 2 (the old-source event must not be stored). " +
            $"Whole archive: {Repr(settled)}");
        Ensure(
            (settled.SizeBytes ?? 0) > 0,
            $"the archive counted 2 events but reports SizeBytes={Repr(settled.SizeBytes)}");
    }

    private static string Repr(long? value) => value?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "None";

    private static string Repr(DescribeArchiveResponse? d) => d is null
        ? "None"
        : $"{{'ArchiveName': '{d.ArchiveName}', 'State': '{d.State?.Value}', 'EventPattern': '{d.EventPattern}', " +
          $"'EventCount': {Repr(d.EventCount)}, 'SizeBytes': {Repr(d.SizeBytes)}}}";

    private static async Task<T> PollUntil<T>(Func<Task<T>> probe, Func<T, bool> predicate, double timeoutS, double intervalS, string description)
        where T : class
    {
        var started = FidelityTime.StartNew();
        T? last = null;
        while (started.Elapsed < TimeSpan.FromSeconds(timeoutS))
        {
            last = await probe();
            if (predicate(last))
            {
                return last;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(intervalS));
        }

        throw Fail($"{description}: not met within {timeoutS:F1}s. Last result: {(last is DescribeArchiveResponse d ? Repr(d) : "None")}");
    }

    private static async Task<DescribeArchiveResponse> WaitUntilStable(Func<Task<DescribeArchiveResponse>> probe, Func<DescribeArchiveResponse, long?> extract,
        double windowS, double intervalS, string description)
    {
        var sample = await probe();
        var baseline = extract(sample);
        var started = FidelityTime.StartNew();
        while (started.Elapsed < TimeSpan.FromSeconds(windowS))
        {
            await FidelityTime.Delay(TimeSpan.FromSeconds(intervalS));
            sample = await probe();
            var current = extract(sample);
            Ensure(
                current == baseline,
                $"{description} changed from {Repr(baseline)} to {Repr(current)} within the {windowS:F1}s " +
                "stability window, so it was still moving");
        }

        return sample;
    }

    private static async Task<DescribeRuleResponse> PollRulePattern(AmazonEventBridgeClient events, string bus, string name, JsonObject expected)
    {
        var started = FidelityTime.StartNew();
        DescribeRuleResponse? last = null;
        while (started.Elapsed < TimeSpan.FromSeconds(RuleConvergeTimeoutS))
        {
            last = await events.DescribeRuleAsync(new DescribeRuleRequest { Name = name, EventBusName = bus });
            if (JsonNode.DeepEquals(JsonNode.Parse(string.IsNullOrEmpty(last.EventPattern) ? "null" : last.EventPattern), expected))
            {
                return last;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(2));
        }

        throw Fail(
            $"the archive's managed rule '{name}' never converged to pattern {expected.ToJsonString()} within " +
            $"{RuleConvergeTimeoutS:F1}s; last read {(last?.EventPattern is { } p ? $"'{p}'" : "None")}");
    }

    /// <summary>DeleteArchive is asynchronous and the bus cannot be deleted while it still exists.</summary>
    private static async Task DeleteArchiveAndWait(AmazonEventBridgeClient events, string name)
    {
        await events.DeleteArchiveAsync(new DeleteArchiveRequest { ArchiveName = name });
        var started = FidelityTime.StartNew();
        while (started.Elapsed < TimeSpan.FromSeconds(ArchiveDeleteTimeoutS))
        {
            try
            {
                await events.DescribeArchiveAsync(new DescribeArchiveRequest { ArchiveName = name });
            }
            catch (AmazonServiceException exc) when (exc.ErrorCode == "ResourceNotFoundException")
            {
                return;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(3));
        }

        throw Fail($"archive {name} still exists {ArchiveDeleteTimeoutS:F1}s after DeleteArchive");
    }
}
