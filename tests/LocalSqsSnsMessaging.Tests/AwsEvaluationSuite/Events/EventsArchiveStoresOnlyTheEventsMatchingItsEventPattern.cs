// Translated from behavioral/events/check_events_archive_stores_only_the_events_matching_its_event_pattern.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.EventBridge.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Events;

/// <summary>events.events-archive-stores-only-the-events-matching-its-event-pattern</summary>
[Category("AwsEvaluationSuite")]
public sealed class EventsArchiveStoresOnlyTheEventsMatchingItsEventPattern
{
    private const string XsvcPrefix = "sim2real-xsvc-";
    private const string ManagedTagKey = "sim2real:managed";

    /// <summary>
    /// An archive with an EventPattern stores only matching events: of three events put once, its
    /// EventCount settles at exactly 2 while an unfiltered archive on the same bus reaches 3.
    /// </summary>
    [Test]
    [DisplayName("events.events-archive-stores-only-the-events-matching-its-event-pattern")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var events = ctx.Events;
        var matchingSource = XName("src-match").Replace('-', '.');
        var otherSource = XName("src-other").Replace('-', '.');

        var bus = XName("bus");
        var busArn = (await events.CreateEventBusAsync(new CreateEventBusRequest { Name = bus, Tags = XsvcTagsKv() })).EventBusArn;
        ctx.OnCleanup(() => ctx.Events.DeleteEventBusAsync(new DeleteEventBusRequest { Name = bus }));

        var filtered = await CreateArchive(ctx, busArn, eventPattern: $$"""{"source": ["{{matchingSource}}"]}""");
        var unfiltered = await CreateArchive(ctx, busArn, eventPattern: null);

        foreach (var name in new[] { filtered, unfiltered })
        {
            var state = await PollUntil(
                () => events.DescribeArchiveAsync(new DescribeArchiveRequest { ArchiveName = name }),
                d => d.State?.Value is "ENABLED" or "CREATE_FAILED" or "DISABLED",
                timeoutS: 300.0,
                intervalS: 3.0,
                description: $"archive {name} left CREATING");
            Ensure(
                state.State?.Value == "ENABLED",
                $"archive {name} settled at State='{state.State?.Value}', expected 'ENABLED'; the whole " +
                $"archive was {state}");
            Ensure(
                state.EventCount == 0,
                $"archive {name} reported EventCount={state.EventCount} before a single event " +
                "was published, so any count measured later is not attributable to this check");
        }

        // One PutEvents, so both archives see all three events at the same instant.
        var resp = await events.PutEventsAsync(new PutEventsRequest
        {
            Entries =
            [
                new PutEventsRequestEntry { Source = matchingSource, DetailType = "Archived", Detail = """{"i": 1}""", EventBusName = bus },
                new PutEventsRequestEntry { Source = matchingSource, DetailType = "Archived", Detail = """{"i": 2}""", EventBusName = bus },
                new PutEventsRequestEntry { Source = otherSource, DetailType = "Archived", Detail = """{"i": 3}""", EventBusName = bus },
            ],
        });
        Ensure(
            resp.FailedEntryCount == 0,
            "PutEvents did not accept all three entries, so the non-matching event's absence from the " +
            $"filtered archive would be a rejected put rather than filtering: {resp}");
        var entries = resp.Entries ?? [];
        Ensure(
            entries.Count == 3 && entries.All(e => !string.IsNullOrEmpty(e.EventId)),
            "PutEvents returned no EventId for every entry, so all three were not really published: " +
            $"{entries}");

        // The barrier: wait for the UNFILTERED archive to account for all three events. EventCount
        // lags PutEvents by minutes on real AWS, hence the long bound.
        var allDesc = await PollUntil(
            () => events.DescribeArchiveAsync(new DescribeArchiveRequest { ArchiveName = unfiltered }),
            d => (d.EventCount ?? 0) >= 3,
            timeoutS: 900.0,
            intervalS: 5.0,
            description:
                $"unfiltered archive {unfiltered} accounted for all 3 published events (real AWS took " +
                "353 s when this check was written)");
        Ensure(
            allDesc.EventCount == 3,
            $"the unfiltered archive on the same bus stored {allDesc.EventCount} events for 3 " +
            $"published, so the barrier itself is wrong: {allDesc}");
        Ensure(
            (allDesc.SizeBytes ?? 0) > 0,
            "the unfiltered archive counted 3 events but reports SizeBytes=" +
            $"{allDesc.SizeBytes}: the count is bookkeeping, nothing was stored");

        // The pipeline has demonstrably finished with all three events (the unfiltered archive proves
        // it), but that does NOT mean the filtered archive's own count has caught up yet -- the two
        // archives' latencies are independent. Poll the filtered archive to ITS OWN target first.
        await PollUntil(
            () => events.DescribeArchiveAsync(new DescribeArchiveRequest { ArchiveName = filtered }),
            d => (d.EventCount ?? 0) >= 2,
            timeoutS: 600.0,
            intervalS: 5.0,
            description: $"filtered archive {filtered} accounted for both of the matching published events");

        // Now that it has reached 2, sample it across a bounded 20 s window and require every sample
        // to agree -- a count still climbing past 2 would make the equality below a race rather than a
        // measurement.
        var second = await WaitUntilStable(
            () => events.DescribeArchiveAsync(new DescribeArchiveRequest { ArchiveName = filtered }),
            d => d.EventCount,
            windowS: 20.0,
            intervalS: 10.0,
            description: $"archive {filtered} EventCount");
        Ensure(
            second.EventCount == 2,
            $"the archive with EventPattern {{'source': ['{matchingSource}']}} settled at EventCount=" +
            $"{second.EventCount}, expected exactly 2. Two of the three published events matched " +
            "that pattern and one did not; an archive that stores everything lands on 3, and one that " +
            $"stores nothing lands on 0. The whole archive was {second}");
        Ensure(
            (second.SizeBytes ?? 0) > 0,
            "the filtered archive counted 2 events but reports SizeBytes=" +
            $"{second.SizeBytes}: the count is bookkeeping, nothing was stored");
        Ensure(
            !string.IsNullOrEmpty(second.EventPattern) && second.EventPattern.Contains(matchingSource, StringComparison.Ordinal),
            "DescribeArchive no longer reports the pattern the filtering was attributed to: " +
            $"EventPattern='{second.EventPattern}'");
    }

    private static List<Tag> XsvcTagsKv() =>
    [
        new Tag { Key = ManagedTagKey, Value = "true" },
        new Tag { Key = "sim2real:prefix", Value = XsvcPrefix.TrimEnd('-') },
    ];

    /// <summary>A unique, prefix-tagged resource name: sim2real-xsvc-&lt;kind&gt;-&lt;uuid&gt;.</summary>
    /// <remarks>ArchiveName is capped at 48 characters by real AWS, so archive kinds stay short.</remarks>
    private static string XName(string kind) => FidelityContext.UniqueName($"{XsvcPrefix}{kind}-");

    /// <summary>
    /// Create an archive on `bus_arn` and register a teardown that WAITS for it to be gone.
    /// DeleteArchive is asynchronous and the bus cannot be deleted while an archive references it.
    /// </summary>
    private static async Task<string> CreateArchive(FidelityContext ctx, string busArn, string? eventPattern)
    {
        var events = ctx.Events;
        var name = XName("arc");
        var request = new CreateArchiveRequest { ArchiveName = name, EventSourceArn = busArn };
        if (eventPattern is not null)
        {
            request.EventPattern = eventPattern;
        }

        await events.CreateArchiveAsync(request);
        ctx.OnCleanup(() => DeleteArchiveAndWait(ctx, name));
        return name;
    }

    private static async Task DeleteArchiveAndWait(FidelityContext ctx, string name, double timeoutS = 180.0)
    {
        var events = ctx.Events;
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

        throw new InvalidOperationException(
            $"archive {name} still present {timeoutS:0.0}s after DeleteArchive; its event bus " +
            "may fail to delete");
    }

    /// <summary>Poll probe() until predicate(result) is truthy; return the final result (raises on timeout).</summary>
    private static async Task<T> PollUntil<T>(Func<Task<T>> probe, Func<T, bool> predicate, double timeoutS = 60.0, double intervalS = 1.0, string description = "condition")
    {
        var deadline = FidelityTime.StartNew();
        T? last = default;
        while (deadline.Elapsed < TimeSpan.FromSeconds(timeoutS))
        {
            last = await probe();
            if (predicate(last))
            {
                return last;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(intervalS));
        }

        throw Fail($"{description} not met within {timeoutS:0.0}s. Last result: {last}");
    }

    /// <summary>
    /// Sample probe() every interval_s across window_s; raise the moment extract() disagrees with
    /// the first sample. Returns the final (fully agreeing) sample -- never a possibly-stale single
    /// read taken once and trusted.
    /// </summary>
    private static async Task<T> WaitUntilStable<T, TValue>(Func<Task<T>> probe, Func<T, TValue> extract, double windowS, double intervalS, string description)
    {
        var sample = await probe();
        var baseline = extract(sample);
        var deadline = FidelityTime.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(windowS))
        {
            await FidelityTime.Delay(TimeSpan.FromSeconds(intervalS));
            sample = await probe();
            var current = extract(sample);
            Ensure(
                EqualityComparer<TValue>.Default.Equals(current, baseline),
                $"{description} changed from {baseline} to {current} within the {windowS:0.0}s " +
                "stability window, so it was still moving and no exact value is measurable here");
        }

        return sample;
    }
}
