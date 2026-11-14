// Translated from behavioral/events/check_put_events_partial_failure_invalid_entry.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.EventBridge.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Events;

/// <summary>events.put-events-partial-failure-invalid-entry</summary>
[Category("AwsEvaluationSuite")]
public sealed class PutEventsPartialFailureInvalidEntry
{
    /// <summary>
    /// PutEvents reports per-entry failures (missing required fields) while still succeeding for
    /// well-formed entries in the same batch: the good entry gets an EventId, the bad entry gets an
    /// ErrorCode, and FailedEntryCount is exactly 1.
    /// </summary>
    [Test]
    [DisplayName("events.put-events-partial-failure-invalid-entry")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    [KnownFidelityGap(FidelityGap.EventBridgePutEvents, "PutEvents accepts an entry with no Source instead of failing it with an ErrorCode")]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ev = ctx.Events;
        var source = FidelityContext.UniqueName("sim2real.behav.source.");
        var resp = await ev.PutEventsAsync(new PutEventsRequest
        {
            Entries =
            [
                new PutEventsRequestEntry { Source = source, DetailType = "Good", Detail = """{"ok": true}""" },
                new PutEventsRequestEntry { DetailType = "Missing Source", Detail = """{"ok": false}""" },
            ],
        });
        Ensure(resp.FailedEntryCount == 1, $"expected exactly 1 failed entry, got {Repr(resp)}");
        var entries = resp.Entries ?? throw new KeyNotFoundException("'Entries'");
        Ensure(
            entries.Count == 2,
            $"PutEvents must return one result per request entry: sent 2, got {entries.Count} -- [{string.Join(", ", entries.Select(Repr))}]");
        Ensure(
            entries[0].EventId is not null && entries[0].ErrorCode is null,
            $"entry 0 was well-formed so it must carry an EventId and no ErrorCode, got {Repr(entries[0])}");
        Ensure(
            entries[1].ErrorCode is not null && entries[1].EventId is null,
            $"entry 1 was missing Source so it must carry an ErrorCode and no EventId, got {Repr(entries[1])}");
    }

    private static string Repr(PutEventsResponse resp) =>
        $"{{'FailedEntryCount': {resp.FailedEntryCount}, 'Entries': [{string.Join(", ", (resp.Entries ?? []).Select(Repr))}]}}";

    private static string Repr(PutEventsResultEntry e) =>
        "{" + string.Join(", ", new[]
        {
            e.EventId is null ? null : $"'EventId': '{e.EventId}'",
            e.ErrorCode is null ? null : $"'ErrorCode': '{e.ErrorCode}'",
            e.ErrorMessage is null ? null : $"'ErrorMessage': '{e.ErrorMessage}'",
        }.Where(s => s is not null)) + "}";
}
