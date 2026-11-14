// Translated from behavioral/events/check_test_event_pattern_string_content_filters.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Text.Json;
using System.Text.Json.Nodes;
using Amazon.EventBridge;
using Amazon.EventBridge.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Events;

/// <summary>events.test-event-pattern-string-content-filters</summary>
[Category("AwsEvaluationSuite")]
public sealed class TestEventPatternStringContentFilters
{
    /// <summary>
    /// prefix, suffix, equals-ignore-case and wildcard each match their anchored target and reject
    /// near-misses (substring, unanchored, wrong case), while exact matching stays case-sensitive.
    /// </summary>
    [Test]
    [DisplayName("events.test-event-pattern-string-content-filters")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ev = ctx.Events;

        // --- prefix: anchored at the START of the string, not "contains" ----------------------
        const string pre = """{"source": [{"prefix": "sim2real.ord"}]}""";
        await Expect(ev, pre, Event(ctx, source: "sim2real.orders"),
            true, "prefix matches a value that starts with the operand");
        await Expect(ev, pre, Event(ctx, source: "sim2real.ord"),
            true, "prefix matches a value equal to the operand");
        await Expect(ev, pre, Event(ctx, source: "x.sim2real.orders"),
            false, "prefix must be anchored at the start, not a substring test");
        await Expect(ev, pre, Event(ctx, source: "sim2real.payments"),
            false, "prefix must not match an unrelated value");
        await Expect(ev, """{"source": [{"prefix": "sim2real.Ord"}]}""", Event(ctx, source: "sim2real.orders"),
            false, "prefix is case-sensitive");

        // --- suffix: anchored at the END ------------------------------------------------------
        const string suf = """{"detail": {"key": [{"suffix": ".json"}]}}""";
        await Expect(ev, suf, Event(ctx, detail: """{"key": "reports/2024/summary.json"}"""),
            true, "suffix matches a value that ends with the operand");
        await Expect(ev, suf, Event(ctx, detail: """{"key": "summary.json.bak"}"""),
            false, "suffix must be anchored at the end, not a substring test");
        await Expect(ev, suf, Event(ctx, detail: """{"key": "summary.JSON"}"""),
            false, "suffix is case-sensitive");

        // --- equals-ignore-case: case-insensitive but still WHOLE-value ----------------------
        const string eic = """{"detail-type": [{"equals-ignore-case": "orderplaced"}]}""";
        await Expect(ev, eic, Event(ctx, detailType: "OrderPlaced"),
            true, "equals-ignore-case matches a differently-cased value");
        await Expect(ev, eic, Event(ctx, detailType: "ORDERPLACED"),
            true, "equals-ignore-case matches an upper-cased value");
        await Expect(ev, eic, Event(ctx, detailType: "OrderPlacedV2"),
            false, "equals-ignore-case compares the whole value, not a prefix");
        await Expect(ev, eic, Event(ctx, detailType: "OrderCancelled"),
            false, "equals-ignore-case must not match a different word");
        // ...and the ordinary exact matcher next door stays case-SENSITIVE.
        await Expect(ev, """{"detail-type": ["orderplaced"]}""", Event(ctx, detailType: "OrderPlaced"),
            false, "exact matching must remain case-sensitive alongside equals-ignore-case");

        // --- wildcard: `*` is zero-or-more characters, and the pattern spans the WHOLE value --
        const string wc = """{"detail": {"path": [{"wildcard": "/api/*/users"}]}}""";
        await Expect(ev, wc, Event(ctx, detail: """{"path": "/api/v1/users"}"""),
            true, "wildcard matches with the star standing for one segment");
        await Expect(ev, wc, Event(ctx, detail: """{"path": "/api//users"}"""),
            true, "wildcard star matches the empty string");
        await Expect(ev, wc, Event(ctx, detail: """{"path": "/api/v1/users/42"}"""),
            false, "wildcard must span the whole value, so a longer value must not match");
        await Expect(ev, wc, Event(ctx, detail: """{"path": "/prefix/api/v1/users"}"""),
            false, "wildcard must be anchored at the start of the value");
        await Expect(ev, wc, Event(ctx, detail: """{"path": "/api/v1/USERS"}"""),
            false, "wildcard is case-sensitive");
        // A star at the end absorbs any tail, including nothing at all.
        const string tail = """{"detail": {"path": [{"wildcard": "/api/*"}]}}""";
        await Expect(ev, tail, Event(ctx, detail: """{"path": "/api/"}"""),
            true, "a trailing star matches an empty tail");
        await Expect(ev, tail, Event(ctx, detail: """{"path": "/api/v1/users/42"}"""),
            true, "a trailing star absorbs an arbitrary tail");
        await Expect(ev, tail, Event(ctx, detail: """{"path": "/internal/api/v1"}"""),
            false, "a trailing star does not make the pattern unanchored at the start");
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
