// Translated from behavioral/events/check_list_archives_filters_by_prefix_and_source.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.EventBridge.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Events;

/// <summary>events.list-archives-filters-by-prefix-and-source</summary>
[Category("AwsEvaluationSuite")]
public sealed class ListArchivesFiltersByPrefixAndSource
{
    private const string CombinedFilterRefusal =
        "At most one filter is allowed for ListArchives. Use either : State, EventSourceArn, or " +
        "NamePrefix.";

    /// <summary>
    /// ListArchives honors NamePrefix and EventSourceArn filters and only
    /// reports the archive that actually exists.
    /// </summary>
    [Test]
    [DisplayName("events.list-archives-filters-by-prefix-and-source")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    [KnownFidelityGap(FidelityGap.EventBridgeArchives, "CreateArchive is not implemented (NotSupportedException)")]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ev = ctx.Events;
        var archiveName = FidelityContext.UniqueName("sim2real-behav-evb-archive-");
        var defaultBusArn = (await ev.DescribeEventBusAsync(new DescribeEventBusRequest { Name = "default" })).Arn;
        await ev.CreateArchiveAsync(new CreateArchiveRequest { ArchiveName = archiveName, EventSourceArn = defaultBusArn });
        ctx.OnCleanup(() => IgnoreNotFound(() => ev.DeleteArchiveAsync(new DeleteArchiveRequest { ArchiveName = archiveName })));

        await PollUntil(
            () => ev.DescribeArchiveAsync(new DescribeArchiveRequest { ArchiveName = archiveName }),
            d => (d.State ?? throw new KeyNotFoundException("'State'")).Value is "ENABLED" or "CREATE_FAILED",
            timeoutS: 60,
            intervalS: 1.0);

        var byPrefix = ArchivesOf(await ev.ListArchivesAsync(new ListArchivesRequest { NamePrefix = archiveName }));
        Ensure(
            byPrefix.Select(a => a.ArchiveName).ToHashSet().SetEquals([archiveName]),
            $"NamePrefix filter broken: {Repr(byPrefix)}");

        // ListArchives accepts at most ONE filter (State | EventSourceArn | NamePrefix);
        // combining them must be rejected rather than silently AND-ed.
        var combinedErr = await Error(() => ev.ListArchivesAsync(new ListArchivesRequest { NamePrefix = archiveName, EventSourceArn = defaultBusArn }));
        Ensure(
            combinedErr == ("ValidationException", CombinedFilterRefusal),
            $"expected ValidationException for multi-filter ListArchives, got {combinedErr}");

        var bySource = ArchivesOf(await ev.ListArchivesAsync(new ListArchivesRequest { EventSourceArn = defaultBusArn }));
        Ensure(
            bySource.Any(a => a.ArchiveName == archiveName),
            $"EventSourceArn filter must find the archive: {Repr(bySource)}");

        var none = ArchivesOf(await ev.ListArchivesAsync(new ListArchivesRequest { NamePrefix = archiveName + "-nope" }));
        Ensure(none.Count == 0, $"expected no archives for unmatched prefix, got {Repr(none)}");

        // Deletes prove gone and unusable: an independent read reports the specific
        // not-found code, and a further mutation on the dead id is rejected too.
        await ev.DeleteArchiveAsync(new DeleteArchiveRequest { ArchiveName = archiveName });
        (string?, string?) notFound = ("ResourceNotFoundException", $"Archive {archiveName} does not exist.");
        var describeErr = await Error(() => ev.DescribeArchiveAsync(new DescribeArchiveRequest { ArchiveName = archiveName }));
        Ensure(
            describeErr == notFound,
            $"expected a deleted archive to be gone with {notFound}, got {describeErr}");
        var redeleteErr = await Error(() => ev.DeleteArchiveAsync(new DeleteArchiveRequest { ArchiveName = archiveName }));
        Ensure(
            redeleteErr == notFound,
            $"expected a second delete of a gone archive to be rejected with {notFound}, " +
            $"got {redeleteErr}");
        var goneFromPrefix = ArchivesOf(await ev.ListArchivesAsync(new ListArchivesRequest { NamePrefix = archiveName }));
        Ensure(
            goneFromPrefix.Count == 0,
            $"deleted archive must not still be reported by NamePrefix, got {Repr(goneFromPrefix)}");
    }

    /// <summary>
    /// Call fn(); return the botocore (Error.Code, Error.Message) of a ClientError. An unexpected
    /// SUCCESS raises, because a target ACCEPTING a call real AWS refuses is the strongest signal
    /// available.
    /// </summary>
    private static async Task<(string? Code, string? Message)> Error<T>(Func<Task<T>> fn)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is not null)
        {
            return (error.Code, error.Message);
        }

        throw Fail($"expected this call to be rejected, but it SUCCEEDED and returned {result}");
    }

    private static async Task IgnoreNotFound(Func<Task> fn)
    {
        var error = await FidelityContext.CaptureError(fn);
        if (error is not null && error.Code != "ResourceNotFoundException")
        {
            throw error.Exception;
        }
    }

    /// <summary>Poll probe() until predicate(result); raise AssertionError on timeout.</summary>
    private static async Task<T> PollUntil<T>(Func<Task<T>> probe, Func<T, bool> predicate, double timeoutS, double intervalS)
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

        throw Fail($"timed out after {timeoutS}s waiting; last={last}");
    }

    /// <summary><c>resp["Archives"]</c>: a missing key raises KeyError, as in Python.</summary>
    private static List<Archive> ArchivesOf(ListArchivesResponse resp) => resp.Archives ?? throw new KeyNotFoundException("'Archives'");

    private static string Repr(IEnumerable<Archive> archives) =>
        $"[{string.Join(", ", archives.Select(a => $"{{'ArchiveName': '{a.ArchiveName}', 'EventSourceArn': '{a.EventSourceArn}', 'State': '{a.State?.Value}'}}"))}]";
}
