// Translated from behavioral/events/check_put_events_detail_that_is_json_but_not_an_object_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.EventBridge.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Events;

/// <summary>events.put-events-detail-that-is-json-but-not-an-object-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class PutEventsDetailThatIsJsonButNotAnObjectRejected
{
    /// <summary>
    /// In one PutEvents batch, array, number and string Details each fail in place with
    /// MalformedDetail "Detail is malformed." while object and empty-object Details get EventIds;
    /// FailedEntryCount is 3.
    /// </summary>
    [Test]
    [DisplayName("events.put-events-detail-that-is-json-but-not-an-object-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    [KnownFidelityGap(FidelityGap.EventBridgePutEvents, "PutEvents accepts array, number and string Details instead of failing each entry with MalformedDetail")]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ev = ctx.Events;
        var source = FidelityContext.UniqueName("sim2real.behav.detail.");
        // json.dumps of {"ok": True}, [1, 2], 5, "text" and {}.
        string[] details =
        [
            """{"ok": true}""",
            "[1, 2]",
            "5",
            "\"text\"",
            "{}",
        ];
        var resp = await ev.PutEventsAsync(new PutEventsRequest
        {
            Entries = [.. details.Select(d => new PutEventsRequestEntry { Source = source, DetailType = "Sim2realDetailShape", Detail = d })],
        });
        var entries = resp.Entries ?? throw new KeyNotFoundException("'Entries'");
        Ensure(entries.Count == details.Length, $"PutEvents returned {entries.Count} entries for {details.Length} sent");
        Ensure(resp.FailedEntryCount == 3, $"FailedEntryCount is {resp.FailedEntryCount}, expected 3");

        foreach (var i in new[] { 0, 4 })
        {
            Ensure(
                entries[i].EventId is not null && entries[i].ErrorCode is null,
                $"entry {i} with object Detail '{details[i]}' was not accepted: {Repr(entries[i])}");
        }

        foreach (var i in new[] { 1, 2, 3 })
        {
            Ensure(
                entries[i].ErrorCode == "MalformedDetail",
                $"entry {i} with non-object Detail '{details[i]}' gave {Repr(entries[i])}, expected MalformedDetail");
            Ensure(
                entries[i].ErrorMessage == "Detail is malformed.",
                $"entry {i} with non-object Detail '{details[i]}' gave message '{entries[i].ErrorMessage}'");
            Ensure(entries[i].EventId is null, $"failed entry {i} still carries an EventId: {Repr(entries[i])}");
        }
    }

    private static string Repr(PutEventsResultEntry e) =>
        "{" + string.Join(", ", new[]
        {
            e.EventId is null ? null : $"'EventId': '{e.EventId}'",
            e.ErrorCode is null ? null : $"'ErrorCode': '{e.ErrorCode}'",
            e.ErrorMessage is null ? null : $"'ErrorMessage': '{e.ErrorMessage}'",
        }.Where(s => s is not null)) + "}";
}
