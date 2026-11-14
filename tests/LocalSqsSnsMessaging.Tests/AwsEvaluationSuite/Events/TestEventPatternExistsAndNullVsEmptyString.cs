// Translated from behavioral/events/check_test_event_pattern_exists_and_null_vs_empty_string.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Text.Json;
using System.Text.Json.Nodes;
using Amazon.EventBridge;
using Amazon.EventBridge.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Events;

/// <summary>events.test-event-pattern-exists-and-null-vs-empty-string</summary>
[Category("AwsEvaluationSuite")]
public sealed class TestEventPatternExistsAndNullVsEmptyString
{
    /// <summary>
    /// `exists` tests presence (a falsy value such as "", 0 or false is present), and absent, JSON null
    /// and the empty string are three distinct values to exact matching.
    /// </summary>
    [Test]
    [DisplayName("events.test-event-pattern-exists-and-null-vs-empty-string")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ev = ctx.Events;

        // --- exists: true ---------------------------------------------------------------------
        const string present = """{"detail": {"customer": [{"exists": true}]}}""";
        await Expect(ev, present, Event(ctx, detail: """{"customer": "C1"}"""),
            true, "exists:true matches a present string leaf");
        await Expect(ev, present, Event(ctx, detail: """{"customer": 0}"""),
            true, "exists:true matches a present numeric leaf, including the falsy value 0");
        await Expect(ev, present, Event(ctx, detail: """{"customer": false}"""),
            true, "exists:true matches a present boolean leaf, including false");
        await Expect(ev, present, Event(ctx, detail: """{"customer": ""}"""),
            true, "exists:true matches a present leaf whose value is the empty string");
        await Expect(ev, present, Event(ctx, detail: """{"other": "C1"}"""),
            false, "exists:true must not match when the field is absent");
        await Expect(ev, present, Event(ctx, detail: "{}"),
            false, "exists:true must not match an empty detail");

        // --- exists: false --------------------------------------------------------------------
        const string absent = """{"detail": {"customer": [{"exists": false}]}}""";
        await Expect(ev, absent, Event(ctx, detail: """{"other": "C1"}"""),
            true, "exists:false matches when the field is absent but a sibling is present");
        await Expect(ev, absent, Event(ctx, detail: """{"customer": "C1"}"""),
            false, "exists:false must not match when the field is present");
        await Expect(ev, absent, Event(ctx, detail: """{"customer": ""}"""),
            false, "exists:false must not match a present-but-empty-string field");
        await Expect(ev, absent, Event(ctx, detail: """{"customer": 0}"""),
            false, "exists:false must not match a present-but-zero field");
        await Expect(ev, absent, Event(ctx, detail: """{"customer": false}"""),
            false, "exists:false must not match a present-but-false field");

        // exists:false composes with an ordinary value key by AND, so both must hold.
        const string composed = """{"source": ["sim2real.orders"], "detail": {"customer": [{"exists": false}]}}""";
        await Expect(ev, composed, Event(ctx, source: "sim2real.orders", detail: """{"other": 1}"""),
            true, "both the value key and the exists:false key are satisfied");
        await Expect(ev, composed, Event(ctx, source: "sim2real.orders", detail: """{"customer": "C1"}"""),
            false, "exists:false unsatisfied fails the whole pattern");
        await Expect(ev, composed, Event(ctx, source: "sim2real.payments", detail: """{"other": 1}"""),
            false, "the value key unsatisfied fails the whole pattern");

        // --- null is a value, and it is not "" and not absence ---------------------------------
        const string nullPat = """{"detail": {"customer": [null]}}""";
        await Expect(ev, nullPat, Event(ctx, detail: """{"customer": null}"""),
            true, "a null pattern value matches a null event value");
        await Expect(ev, nullPat, Event(ctx, detail: """{"customer": ""}"""),
            false, "a null pattern value must NOT match the empty string");
        await Expect(ev, nullPat, Event(ctx, detail: """{"customer": "null"}"""),
            false, "a null pattern value must NOT match the string \"null\"");
        await Expect(ev, nullPat, Event(ctx, detail: "{}"),
            false, "a null pattern value must NOT match an absent field");

        // --- the empty string is a value too --------------------------------------------------
        const string emptyPat = """{"detail": {"customer": [""]}}""";
        await Expect(ev, emptyPat, Event(ctx, detail: """{"customer": ""}"""),
            true, "an empty-string pattern value matches an empty-string event value");
        await Expect(ev, emptyPat, Event(ctx, detail: """{"customer": null}"""),
            false, "an empty-string pattern value must NOT match null");
        await Expect(ev, emptyPat, Event(ctx, detail: "{}"),
            false, "an empty-string pattern value must NOT match an absent field");
        await Expect(ev, emptyPat, Event(ctx, detail: """{"customer": " "}"""),
            false, "an empty-string pattern value must NOT match a single space");
    }

    /// <summary>A complete, well-formed EventBridge envelope, with any field overridable.</summary>
    /// <remarks><paramref name="detail"/> and <paramref name="resources"/> are JSON text.</remarks>
    private static JsonObject Event(FidelityContext ctx, string? source = null, string? detailType = null, string? detail = null, string? resources = null)
    {
        var @event = new JsonObject
        {
            ["id"] = "11111111-2222-3333-4444-555555555555",
            ["version"] = "0",
            ["detail-type"] = "OrderPlaced",
            ["source"] = "sim2real.orders",
            ["account"] = "123456789012",
            ["time"] = "2024-01-01T00:00:00Z",
            ["region"] = ctx.Region,
            ["resources"] = new JsonArray(),
            ["detail"] = new JsonObject(),
        };
        if (source is not null)
        {
            @event["source"] = source;
        }

        if (detailType is not null)
        {
            @event["detail-type"] = detailType;
        }

        if (resources is not null)
        {
            @event["resources"] = JsonNode.Parse(resources);
        }

        if (detail is not null)
        {
            @event["detail"] = JsonNode.Parse(detail);
        }

        return @event;
    }

    /// <summary>TestEventPattern's boolean verdict for one (pattern, event) pair.</summary>
    private static async Task<bool> Matches(AmazonEventBridgeClient ev, string pattern, JsonObject @event)
    {
        var resp = await ev.TestEventPatternAsync(new TestEventPatternRequest { EventPattern = pattern, Event = PyDumps(@event) });
        var result = resp.Result;
        Ensure(result is not null, "TestEventPattern.Result must be a JSON boolean, got None (NoneType)");
        return result.Value;
    }

    /// <summary>Assert TestEventPattern's verdict, reporting the pattern and event that disagreed.</summary>
    private static async Task Expect(AmazonEventBridgeClient ev, string pattern, JsonObject @event, bool want, string why)
    {
        var got = await Matches(ev, pattern, @event);
        Ensure(
            got == want,
            $"{why}: expected Result={want}, got {got}\n" +
            $"  pattern = {pattern}\n" +
            $"  event   = {PyDumps(@event)}");
    }

    /// <summary><c>json.dumps(value)</c> with Python's default separators (", " and ": ").</summary>
    private static string PyDumps(JsonNode? node) => node switch
    {
        null => "null",
        JsonObject o => "{" + string.Join(", ", o.Select(kv => $"{JsonSerializer.Serialize(kv.Key)}: {PyDumps(kv.Value)}")) + "}",
        JsonArray a => "[" + string.Join(", ", a.Select(PyDumps)) + "]",
        _ => node.ToJsonString(),
    };
}
