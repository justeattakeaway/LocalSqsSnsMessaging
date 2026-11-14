// Translated from behavioral/events/check_test_event_pattern_anything_but_nested_suffix_equals_ignore_case_and_wildcard_forms.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Text.Json;
using System.Text.Json.Nodes;
using Amazon.EventBridge;
using Amazon.EventBridge.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Events;

/// <summary>events.test-event-pattern-anything-but-nested-suffix-equals-ignore-case-and-wildcard-forms</summary>
[Category("AwsEvaluationSuite")]
public sealed class TestEventPatternAnythingButNestedSuffixEqualsIgnoreCaseAndWildcardForms
{
    /// <summary>
    /// `anything-but` nested with `suffix`, `equals-ignore-case` or `wildcard` excludes every value the
    /// inner comparator would match and admits every value it would not.
    /// </summary>
    [Test]
    [DisplayName("events.test-event-pattern-anything-but-nested-suffix-equals-ignore-case-and-wildcard-forms")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ev = ctx.Events;

        // --- anything-but nested suffix: excludes every value ENDING with the operand -----------
        var suf = JsonNode.Parse("""{"detail": {"key": [{"anything-but": {"suffix": ".tmp"}}]}}""")!;
        await Expect(ev, suf, Event(ctx, detail: """{"key": "report.json"}"""),
            true, "a value not ending with the excluded suffix is admitted");
        await Expect(ev, suf, Event(ctx, detail: """{"key": "report.tmp"}"""),
            false, "a value ending with the excluded suffix must be excluded");
        await Expect(ev, suf, Event(ctx, detail: """{"key": "report.TMP"}"""),
            true, "the nested suffix comparison stays case-sensitive, so a recased suffix is admitted");

        // --- anything-but nested equals-ignore-case: excludes every value equal UP TO CASE -------
        var eic = JsonNode.Parse("""{"detail": {"state": [{"anything-but": {"equals-ignore-case": "blocked"}}]}}""")!;
        await Expect(ev, eic, Event(ctx, detail: """{"state": "running"}"""),
            true, "a value that is not a case-variant of the excluded word is admitted");
        await Expect(ev, eic, Event(ctx, detail: """{"state": "Blocked"}"""),
            false, "a differently-cased variant of the excluded word must be excluded");
        await Expect(ev, eic, Event(ctx, detail: """{"state": "BLOCKED"}"""),
            false, "the upper-cased variant of the excluded word must be excluded too");

        // --- anything-but nested wildcard: excludes every value the glob spans -------------------
        var wc = JsonNode.Parse("""{"detail": {"path": [{"anything-but": {"wildcard": "/internal/*"}}]}}""")!;
        await Expect(ev, wc, Event(ctx, detail: """{"path": "/public/users"}"""),
            true, "a value outside the excluded glob is admitted");
        await Expect(ev, wc, Event(ctx, detail: """{"path": "/internal/audit"}"""),
            false, "a value the excluded glob spans must be excluded");
        await Expect(ev, wc, Event(ctx, detail: """{"path": "/internal/"}"""),
            false, "the glob's star matches an empty tail, so the bare prefix is excluded too");
        await Expect(ev, wc, Event(ctx, detail: """{"path": "/other/internal/audit"}"""),
            true, "the excluded glob is anchored at the start, so this value is admitted");
    }

    /// <summary>A complete, well-formed EventBridge envelope, with any field overridable.</summary>
    private static JsonObject Event(FidelityContext ctx, string? detail = null, string? source = null)
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
        if (detail is not null)
        {
            @event["detail"] = JsonNode.Parse(detail);
        }

        if (source is not null)
        {
            @event["source"] = source;
        }

        return @event;
    }

    /// <summary>TestEventPattern's boolean verdict for one (pattern, event) pair.</summary>
    private static async Task<bool> Matches(AmazonEventBridgeClient ev, JsonNode pattern, JsonNode @event)
    {
        var resp = await ev.TestEventPatternAsync(new TestEventPatternRequest { EventPattern = PyDumps(pattern), Event = PyDumps(@event) });
        var result = resp.Result;
        Ensure(
            result is not null,
            $"TestEventPattern.Result must be a JSON boolean, got None (NoneType)");
        return result.Value;
    }

    /// <summary>Assert TestEventPattern's verdict, reporting the pattern and event that disagreed.</summary>
    private static async Task Expect(AmazonEventBridgeClient ev, JsonNode pattern, JsonNode @event, bool want, string why)
    {
        var got = await Matches(ev, pattern, @event);
        Ensure(
            got == want,
            $"{why}: expected Result={Py(want)}, got {Py(got)}\n" +
            $"  pattern = {PyDumps(pattern)}\n" +
            $"  event   = {PyDumps(@event)}");
    }

    private static string Py(bool value) => value ? "True" : "False";

    /// <summary><c>json.dumps</c>: ", " and ": " separators; numbers keep the text they were parsed from.</summary>
    private static string PyDumps(JsonNode? node) => node switch
    {
        null => "null",
        JsonObject o => "{" + string.Join(", ", o.Select(kv => $"{JsonSerializer.Serialize(kv.Key)}: {PyDumps(kv.Value)}")) + "}",
        JsonArray a => "[" + string.Join(", ", a.Select(PyDumps)) + "]",
        _ => node.ToJsonString(),
    };
}
