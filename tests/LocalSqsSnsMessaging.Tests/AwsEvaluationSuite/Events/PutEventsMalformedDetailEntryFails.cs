// Translated from behavioral/events/check_put_events_malformed_detail_entry_fails.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.EventBridge.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Events;

/// <summary>events.put-events-malformed-detail-entry-fails</summary>
[Category("AwsEvaluationSuite")]
public sealed class PutEventsMalformedDetailEntryFails
{
    /// <summary>
    /// A PutEvents entry whose Detail is not valid JSON fails at the entry level with
    /// MalformedDetail and no EventId, while a well-formed entry in the same batch succeeds.
    /// </summary>
    [Test]
    [DisplayName("events.put-events-malformed-detail-entry-fails")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    [KnownFidelityGap(FidelityGap.EventBridgePutEvents, "PutEvents accepts a Detail that is not JSON instead of failing the entry with MalformedDetail 'Detail is malformed.'")]
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
                new PutEventsRequestEntry { Source = source, DetailType = "test", Detail = "{not json" },
            ],
        });
        Ensure(resp.FailedEntryCount == 1, $"expected exactly 1 failed entry, got {Repr(resp)}");
        var entries = resp.Entries ?? throw new KeyNotFoundException("'Entries'");
        Ensure(entries.Count == 2, $"expected one result per submitted entry, got {Repr(resp)}");
        var (good, bad) = (entries[0], entries[1]);
        Ensure(
            good.EventId is not null && good.ErrorCode is null,
            $"positive control (well-formed) entry unexpectedly failed: {Repr(good)}");
        Ensure(
            (bad.ErrorCode, bad.ErrorMessage) == ("MalformedDetail", "Detail is malformed."),
            $"expected MalformedDetail, got {Repr(bad)}");
        Ensure(bad.EventId is null, $"malformed entry must not carry an EventId: {Repr(bad)}");
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
