// Translated from behavioral/events/check_list_replays_combined_filters_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.EventBridge;
using Amazon.EventBridge.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Events;

/// <summary>events.list-replays-combined-filters-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class ListReplaysCombinedFiltersRejected
{
    private static readonly (string? Code, string? Message) CombinedFilterRefusal = (
        "ValidationException",
        "At most one filter is allowed for ListReplays. Use either : State, EventSourceArn, or " +
        "NamePrefix.");

    /// <summary>
    /// ListReplays accepts one of NamePrefix, State or EventSourceArn: each alone succeeds,
    /// but every pairing is rejected with ValidationException, though the model declares no
    /// such constraint.
    /// </summary>
    [Test]
    [DisplayName("events.list-replays-combined-filters-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ev = ctx.Events;
        var bus = FidelityContext.UniqueName("sim2real-behav-bus-");
        var busArn = (await ev.CreateEventBusAsync(new CreateEventBusRequest { Name = bus, Tags = [new Tag { Key = "sim2real:managed", Value = "true" }] })).EventBusArn;
        ctx.OnCleanup(() => ev.DeleteEventBusAsync(new DeleteEventBusRequest { Name = bus }));

        var archiveName = FidelityContext.UniqueName("sim2real-behav-archive-");
        var archiveArn = (await ev.CreateArchiveAsync(new CreateArchiveRequest { ArchiveName = archiveName, EventSourceArn = busArn })).ArchiveArn;
        ctx.OnCleanup(() => ev.DeleteArchiveAsync(new DeleteArchiveRequest { ArchiveName = archiveName }));

        var prefix = FidelityContext.UniqueName("sim2real-behav-replay-");

        // Positive controls: each filter alone succeeds.
        await ev.ListReplaysAsync(new ListReplaysRequest { NamePrefix = prefix });
        await ev.ListReplaysAsync(new ListReplaysRequest { State = ReplayState.COMPLETED });
        await ev.ListReplaysAsync(new ListReplaysRequest { EventSourceArn = archiveArn });

        // Negative: every pairing of two filters together is rejected, not just one combination.
        var err = await Error(() => ev.ListReplaysAsync(new ListReplaysRequest { NamePrefix = prefix, State = ReplayState.COMPLETED }));
        Ensure(err == CombinedFilterRefusal, $"NamePrefix+State together: expected {CombinedFilterRefusal}, got {err}");
        err = await Error(() => ev.ListReplaysAsync(new ListReplaysRequest { NamePrefix = prefix, EventSourceArn = archiveArn }));
        Ensure(err == CombinedFilterRefusal, $"NamePrefix+EventSourceArn together: expected {CombinedFilterRefusal}, got {err}");
        err = await Error(() => ev.ListReplaysAsync(new ListReplaysRequest { State = ReplayState.COMPLETED, EventSourceArn = archiveArn }));
        Ensure(err == CombinedFilterRefusal, $"State+EventSourceArn together: expected {CombinedFilterRefusal}, got {err}");

        // Positive control, repeated after every rejection: a single filter still works, so the
        // rejections above are a validation rule, not an archive/bus the account merely lacks.
        var resp = await ev.ListReplaysAsync(new ListReplaysRequest { NamePrefix = prefix });
        var replays = resp.Replays ?? throw new KeyNotFoundException("'Replays'");
        Ensure(replays.Count == 0, $"expected no replays under a fresh unique prefix, got [{string.Join(", ", replays.Select(r => r.ReplayName))}]");
    }

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
}
