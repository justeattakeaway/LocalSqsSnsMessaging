// Translated from behavioral/events/check_put_events_partial_failure_positional_alignment.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Text.RegularExpressions;
using Amazon.EventBridge.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Events;

/// <summary>events.put-events-partial-failure-positional-alignment</summary>
[Category("AwsEvaluationSuite")]
public sealed partial class PutEventsPartialFailurePositionalAlignment
{
    private const string Check = "put-events-partial-failure-positional-alignment";

    [GeneratedRegex("^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$")]
    private static partial Regex UuidRe();

    /// <summary>
    /// A mixed PutEvents batch returns per-entry results aligned to the request (EventId on valid,
    /// InvalidArgument on invalid entries), an 11-entry batch is refused whole with
    /// ValidationException, and an entry on a rule-less bus is still accepted with an EventId.
    /// </summary>
    [Test]
    [DisplayName("events.put-events-partial-failure-positional-alignment")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    [KnownFidelityGap(FidelityGap.EventBridgePutEvents, "PutEvents accepts entries with no Source or no DetailType: FailedEntryCount is 0 instead of 2")]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ev = ctx.Events;

        // A dedicated bus with no rules, so nothing is delivered and default-bus rules cannot interfere.
        var bus = FidelityContext.UniqueName("sim2real-behav-");
        await ev.CreateEventBusAsync(new CreateEventBusRequest { Name = bus, Tags = [new Tag { Key = "sim2real-check", Value = Check }] });
        ctx.OnCleanup(() => ev.DeleteEventBusAsync(new DeleteEventBusRequest { Name = bus }));

        Ensure(
            ((await ev.ListRulesAsync(new ListRulesRequest { EventBusName = bus })).Rules ?? []).Count == 0,
            $"a freshly created bus '{bus}' must have no rules");

        // Invalid entries are non-adjacent and neither first nor last, so a compacted or reordered
        // response cannot pass.
        PutEventsRequestEntry Good(string detail) => new() { Source = "sim2real.orders", DetailType = "OrderPlaced", Detail = detail, EventBusName = bus };
        List<PutEventsRequestEntry> entries =
        [
            Good("""{"n": 0}"""),
            // index 1: no Source
            new PutEventsRequestEntry { DetailType = "OrderPlaced", Detail = """{"n": 1}""", EventBusName = bus },
            Good("""{"n": 2}"""),
            // index 3: no DetailType
            new PutEventsRequestEntry { Source = "sim2real.orders", Detail = """{"n": 3}""", EventBusName = bus },
            Good("""{"n": 4}"""),
        ];
        var resp = await ev.PutEventsAsync(new PutEventsRequest { Entries = entries });
        var results = resp.Entries ?? throw new KeyNotFoundException("'Entries'");

        Ensure(
            results.Count == entries.Count,
            $"PutEvents must return one result per request entry: sent {entries.Count}, got " +
            $"{results.Count} -- {Json(results)}");
        Ensure(
            resp.FailedEntryCount == 2,
            $"expected FailedEntryCount=2, got {resp.FailedEntryCount} -- " +
            $"{Json(results)}");

        foreach (var i in new[] { 0, 2, 4 })
        {
            var entry = results[i];
            Ensure(
                entry.ErrorCode is null,
                $"entry {i} was valid but came back with ErrorCode='{entry.ErrorCode}': " +
                $"{Json(entry)}");
            var eventId = entry.EventId;
            Ensure(
                eventId is not null && UuidRe().IsMatch(eventId),
                $"entry {i} was valid so it must carry a UUID EventId, got '{eventId}'");
        }

        var failureByIndex = new Dictionary<int, (string Parameter, string Why)>
        {
            [1] = ("Source", "an entry with no Source"),
            [3] = ("DetailType", "an entry with no DetailType"),
        };
        foreach (var (i, (parameter, why)) in failureByIndex)
        {
            var entry = results[i];
            Ensure(
                entry.ErrorCode == "InvalidArgument",
                $"index {i} is {why}, so it must fail with ErrorCode=InvalidArgument, got " +
                $"'{entry.ErrorCode}': {Json(entry)}");
            var message = entry.ErrorMessage ?? "";
            Ensure(
                message.Contains(parameter, StringComparison.Ordinal),
                $"index {i} is {why}, so its ErrorMessage must name the offending parameter " +
                $"'{parameter}'; got '{message}'. A per-entry error that does not identify the field " +
                $"is not actionable for the caller.");
            Ensure(
                entry.EventId is null,
                $"index {i} failed, so it must NOT carry an EventId; got '{entry.EventId}'. " +
                $"An EventId on a failed entry makes the failure invisible to a caller that keys off " +
                $"EventId presence.");
        }

        // PutEvents is not idempotent: a re-send keeps the shape but mints fresh EventIds.
        var resp2 = await ev.PutEventsAsync(new PutEventsRequest { Entries = entries });
        Ensure(
            resp2.FailedEntryCount == 2,
            $"the second identical PutEvents returned FailedEntryCount={resp2.FailedEntryCount}");
        var resp2Entries = resp2.Entries ?? throw new KeyNotFoundException("'Entries'");
        Ensure(
            resp2Entries.Select(e => e.ErrorCode is not null).SequenceEqual([false, true, false, true, false]),
            $"the second call's failure positions differ from the first: " +
            $"{Json(resp2Entries)}");
        var firstIds = new[] { 0, 2, 4 }.Select(i => results[i].EventId ?? throw new KeyNotFoundException("'EventId'")).ToList();
        var secondIds = new[] { 0, 2, 4 }.Select(i => resp2Entries[i].EventId ?? throw new KeyNotFoundException("'EventId'")).ToList();
        Ensure(
            !firstIds.Intersect(secondIds).Any(),
            $"PutEvents is not idempotent, so a re-send must mint fresh EventIds; got overlap between " +
            $"[{string.Join(", ", firstIds.Select(x => $"'{x}'"))}] and [{string.Join(", ", secondIds.Select(x => $"'{x}'"))}]");

        // A limit violation is a whole-request refusal, not a partial failure.
        var oversized = Enumerable.Range(0, 11).Select(n => Good($$"""{"n": {{n}}}""")).ToList();
        var (code, errorMessage) = await Error(() => ev.PutEventsAsync(new PutEventsRequest { Entries = oversized }));
        Ensure(
            code == "ValidationException",
            $"PutEvents must refuse an 11-entry batch outright (the modeled maximum is 10): expected " +
            $"ValidationException, got {code}. A partial acceptance here would be the worst outcome -- " +
            $"the caller cannot tell which 10 of its 11 events survived.");
        // The message echoes every entry through a server-side type name: assert its fixed head and end.
        Ensure(errorMessage.StartsWith("1 validation error detected: Value '[", StringComparison.Ordinal), errorMessage);
        Ensure(
            errorMessage.EndsWith(
                "]' at 'entries' failed to satisfy constraint: Member must have length less than or equal to 10",
                StringComparison.Ordinal),
            errorMessage);

        // An EventId means accepted, not delivered: this bus has no rules.
        var lone = await ev.PutEventsAsync(new PutEventsRequest { Entries = [Good("""{"n": "no-rules"}""")] });
        Ensure(
            lone.FailedEntryCount == 0,
            $"a well-formed entry on a bus with no rules must still be accepted, got " +
            $"{{\"FailedEntryCount\": {lone.FailedEntryCount}, \"Entries\": {Json(lone.Entries ?? [])}}}");
        var loneEntries = lone.Entries ?? throw new KeyNotFoundException("'Entries'");
        Ensure(
            UuidRe().IsMatch(loneEntries[0].EventId ?? ""),
            $"an accepted entry on a rule-less bus must still be assigned an EventId, got " +
            $"'{loneEntries[0].EventId}'");
        Ensure(
            ((await ev.ListRulesAsync(new ListRulesRequest { EventBusName = bus })).Rules ?? []).Count == 0,
            "PutEvents must not create rules on the target bus");
    }

    /// <summary>Run `call` and return the AWS error (code, message) it raised; an unexpected success raises.</summary>
    private static async Task<(string Code, string Message)> Error<T>(Func<Task<T>> call)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(call);
        if (error is not null)
        {
            return (error.Code ?? "<missing Error.Code>", error.Message ?? "");
        }

        throw Fail($"expected real AWS to refuse this call, but it SUCCEEDED and returned {result}");
    }

    /// <summary><c>json.dumps(entry)</c> of a botocore result entry.</summary>
    private static string Json(PutEventsResultEntry e) =>
        "{" + string.Join(", ", new[]
        {
            e.EventId is null ? null : $"\"EventId\": \"{e.EventId}\"",
            e.ErrorCode is null ? null : $"\"ErrorCode\": \"{e.ErrorCode}\"",
            e.ErrorMessage is null ? null : $"\"ErrorMessage\": \"{e.ErrorMessage}\"",
        }.Where(s => s is not null)) + "}";

    private static string Json(IEnumerable<PutEventsResultEntry> entries) => $"[{string.Join(", ", entries.Select(Json))}]";
}
