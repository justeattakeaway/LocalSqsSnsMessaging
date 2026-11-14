// Translated from behavioral/events/check_events_create_archive_with_an_invalid_event_pattern_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.EventBridge.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Events;

/// <summary>events.events-create-archive-with-an-invalid-event-pattern-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class EventsCreateArchiveWithAnInvalidEventPatternRejected
{
    private const string XsvcPrefix = "sim2real-xsvc-";
    private const string ManagedTagKey = "sim2real:managed";

    /// <summary>
    /// CreateArchive with an unrecognised pattern operator fails with InvalidEventPatternException
    /// and creates nothing, while the corrected pattern yields an ENABLED archive that ingests an event.
    ///
    /// Reference: https://docs.aws.amazon.com/eventbridge/latest/APIReference/API_CreateArchive.html
    /// </summary>
    [Test]
    [DisplayName("events.events-create-archive-with-an-invalid-event-pattern-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var events = ctx.Events;
        var source = XName("src").Replace('-', '.');

        var bus = XName("bus");
        var busArn = (await events.CreateEventBusAsync(new CreateEventBusRequest { Name = bus, Tags = XsvcTagsKv() })).EventBusArn;
        ctx.OnCleanup(() => ctx.Events.DeleteEventBusAsync(new DeleteEventBusRequest { Name = bus }));

        // Break exactly one thing: an unrecognised matcher operator inside an otherwise well-formed
        // pattern. The name is valid, the EventSourceArn is valid, the JSON parses.
        var rejectedName = XName("arc-bad");
        Ensure(
            rejectedName.Length <= 48,
            $"ArchiveName is capped at 48 characters by real AWS and this one is {rejectedName.Length}: " +
            $"'{rejectedName}' -- a name-length rejection would mask the pattern rejection under test");
        var badPatternRepr = $"{{'source': ['{source}'], 'detail': {{'n': [{{'nosuchop': [1]}}]}}}}";
        var badJson = $$$"""{"source": ["{{{source}}}"], "detail": {"n": [{"nosuchop": [1]}]}}""";
        var (err, unexpected) = await Error(() => events.CreateArchiveAsync(new CreateArchiveRequest
        {
            ArchiveName = rejectedName,
            EventSourceArn = busArn,
            EventPattern = badJson,
        }));
        if (unexpected is not null)
        {
            // The service wrongly accepted it, so the archive EXISTS -- register its teardown before the
            // assertion below aborts the check, or it leaks and blocks the bus delete.
            ctx.OnCleanup(() => DeleteArchiveAndWait(ctx, rejectedName));
        }

        // The parser echoes the pattern and the 1-based column just past the operator's first value.
        (string?, string?) expected = (
            "InvalidEventPatternException",
            $"Event pattern is not valid. Reason: Unrecognized match type nosuchop\n at [Source: " +
            $"(String)\"{badJson}\"; line: 1, column: {badJson.IndexOf("[1]", StringComparison.Ordinal) + 2}]");
        Ensure(
            err == expected,
            $"CreateArchive with EventPattern {badPatternRepr} settled on {err}, expected " +
            $"{expected}. The response that came back instead was {unexpected?.ToString() ?? "None"}");

        // Effect absent, both directions.
        var listed = await ListArchiveNames(events, busArn);
        Ensure(
            !listed.Contains(rejectedName),
            $"the refused CreateArchive still created archive '{rejectedName}': ListArchives over " +
            $"{busArn} returned {Repr(listed)}");
        var (gone, still) = await Error(() => events.DescribeArchiveAsync(new DescribeArchiveRequest { ArchiveName = rejectedName }));
        Ensure(
            gone == ("ResourceNotFoundException", $"Archive {rejectedName} does not exist."),
            $"DescribeArchive on the refused archive '{rejectedName}' settled on {gone}, expected " +
            $"'ResourceNotFoundException'; the response was {still?.ToString() ?? "None"}");

        // Positive control, same file: the corrected form of the SAME pattern creates an archive that
        // reaches ENABLED and really ingests a matching event.
        var goodName = XName("arc-ok");
        await events.CreateArchiveAsync(new CreateArchiveRequest
        {
            ArchiveName = goodName,
            EventSourceArn = busArn,
            EventPattern = $$$"""{"source": ["{{{source}}}"], "detail": {"n": [{"numeric": [">", 0]}]}}""",
        });
        ctx.OnCleanup(() => DeleteArchiveAndWait(ctx, goodName));
        var enabled = await PollUntil(
            () => events.DescribeArchiveAsync(new DescribeArchiveRequest { ArchiveName = goodName }),
            d => d.State?.Value is "ENABLED" or "CREATE_FAILED" or "DISABLED",
            timeoutS: 300.0,
            intervalS: 3.0,
            description: $"control archive {goodName} left CREATING");
        Ensure(
            enabled.State?.Value == "ENABLED",
            $"the control archive settled at State='{enabled.State?.Value}', expected 'ENABLED': " +
            $"{Repr(enabled)}");
        Ensure(
            (await ListArchiveNames(events, busArn)).Contains(goodName),
            $"the control archive '{goodName}' is absent from ListArchives over {busArn}, so the " +
            $"membership assertion above cannot distinguish a refused create from a broken list");

        var resp = await events.PutEventsAsync(new PutEventsRequest
        {
            Entries =
            [
                new PutEventsRequestEntry
                {
                    Source = source,
                    DetailType = "Archived",
                    Detail = """{"n": 7}""",
                    EventBusName = bus,
                },
            ],
        });
        Ensure(
            resp.FailedEntryCount == 0 && !string.IsNullOrEmpty((resp.Entries ?? throw new KeyNotFoundException("'Entries'"))[0].EventId),
            $"the control event was not published, so the ingest assertion below would be vacuous: " +
            $"FailedEntryCount={resp.FailedEntryCount}");
        // Archive ingest lags PutEvents by minutes; there is nothing faster to poll.
        var ingested = await PollUntil(
            () => events.DescribeArchiveAsync(new DescribeArchiveRequest { ArchiveName = goodName }),
            d => (d.EventCount ?? 0) >= 1,
            timeoutS: 900.0,
            intervalS: 5.0,
            description:
                $"control archive {goodName} ingested the matching event (real AWS took 151-353 s when " +
                $"this check was written)");
        Ensure(
            ingested.EventCount == 1,
            $"the control archive stored {ingested.EventCount} events for 1 published: " +
            $"{Repr(ingested)}");
        Ensure(
            (ingested.SizeBytes ?? 0) > 0,
            $"the control archive counted the event but reports SizeBytes=" +
            $"{ingested.SizeBytes}: the count is bookkeeping, nothing was stored, and the " +
            $"'valid pattern works' half of this negative would be satisfied by an inert record");
    }

    private static Dictionary<string, string> XsvcResourceTags() =>
        new() { [ManagedTagKey] = "true", ["sim2real:prefix"] = XsvcPrefix.TrimEnd('-') };

    private static List<Tag> XsvcTagsKv() => [.. XsvcResourceTags().Select(kv => new Tag { Key = kv.Key, Value = kv.Value })];

    /// <summary>
    /// A unique, prefix-tagged resource name: sim2real-xsvc-&lt;kind&gt;-&lt;uuid&gt;.
    ///
    /// ArchiveName is capped at 48 characters by real AWS, so archive kinds stay short.
    /// </summary>
    private static string XName(string kind) => FidelityContext.UniqueName($"{XsvcPrefix}{kind}-");

    /// <summary>
    /// Return ((error code, message), None) for a call that fails, and a sentinel that cannot be
    /// mistaken for an error plus the response when the call unexpectedly SUCCEEDS.
    /// </summary>
    /// <remarks>
    /// Returning a falsy sentinel on success would turn the strongest possible signal -- an emulator
    /// accepting input real AWS refuses -- into something that reads like a naming difference.
    /// </remarks>
    private static async Task<((string? Code, string? Message) Error, object? Response)> Error<T>(Func<Task<T>> fn)
        where T : class
    {
        var (resp, error) = await FidelityContext.Capture(fn);
        if (error is not null)
        {
            return ((error.Code, error.Message), null);
        }

        return (("<call unexpectedly SUCCEEDED>", null), resp);
    }

    /// <summary>
    /// Every archive name on `bus_arn`, following NextToken by hand.
    ///
    /// ListArchives has no botocore paginator, and other archives can push ours off page one.
    /// </summary>
    private static async Task<List<string>> ListArchiveNames(Amazon.EventBridge.AmazonEventBridgeClient events, string busArn)
    {
        var names = new List<string>();
        string? token = null;
        while (true)
        {
            var request = new ListArchivesRequest { EventSourceArn = busArn };
            if (!string.IsNullOrEmpty(token))
            {
                request.NextToken = token;
            }

            var page = await events.ListArchivesAsync(request);
            names.AddRange((page.Archives ?? []).Select(a => a.ArchiveName));
            token = page.NextToken;
            if (string.IsNullOrEmpty(token))
            {
                return names;
            }
        }
    }

    /// <summary>
    /// Delete an archive and wait for it to be gone: the event bus cannot be deleted while an
    /// archive still references it.
    /// </summary>
    private static async Task DeleteArchiveAndWait(FidelityContext ctx, string name, double timeoutS = 180.0)
    {
        var events = ctx.Events;
        var error = await FidelityContext.CaptureError(() => events.DeleteArchiveAsync(new DeleteArchiveRequest { ArchiveName = name }));
        if (error is not null && error.Code != "ResourceNotFoundException")
        {
            throw new InvalidOperationException($"could not delete archive {name}: {error}", error.Exception);
        }

        var started = FidelityTime.StartNew();
        while (started.Elapsed < TimeSpan.FromSeconds(timeoutS))
        {
            var describeError = await FidelityContext.CaptureError(() => events.DescribeArchiveAsync(new DescribeArchiveRequest { ArchiveName = name }));
            if (describeError?.Code == "ResourceNotFoundException")
            {
                return;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(3));
        }

        throw new InvalidOperationException(
            $"archive {name} still present {timeoutS}s after DeleteArchive; its event bus " +
            $"may fail to delete");
    }

    /// <summary>Poll probe() until predicate(result) is truthy; return the final result (raises on timeout).</summary>
    private static async Task<T> PollUntil<T>(
        Func<Task<T>> probe, Func<T, bool> predicate, double timeoutS = 60.0, double intervalS = 1.0, string description = "condition")
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

        throw Fail($"{description} not met within {timeoutS}s. Last result: {(last is DescribeArchiveResponse d ? Repr(d) : last?.ToString() ?? "None")}");
    }

    private static string Repr(DescribeArchiveResponse d) =>
        $"{{'ArchiveName': '{d.ArchiveName}', 'State': '{d.State?.Value}', 'EventCount': {d.EventCount}, 'SizeBytes': {d.SizeBytes}}}";

    private static string Repr(IEnumerable<string> items) => $"[{string.Join(", ", items.Select(i => $"'{i}'"))}]";
}
