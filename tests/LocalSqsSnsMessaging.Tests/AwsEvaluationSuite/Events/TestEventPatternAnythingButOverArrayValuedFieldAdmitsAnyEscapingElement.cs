// Translated from behavioral/events/check_test_event_pattern_anything_but_over_array_valued_field_admits_any_escaping_element.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Text.Json;
using System.Text.Json.Nodes;
using Amazon.EventBridge;
using Amazon.EventBridge.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Events;

/// <summary>events.test-event-pattern-anything-but-over-array-valued-field-admits-any-escaping-element</summary>
[Category("AwsEvaluationSuite")]
public sealed class TestEventPatternAnythingButOverArrayValuedFieldAdmitsAnyEscapingElement
{
    /// <summary>
    /// `anything-but` over an array-valued field admits the event when any element escapes the
    /// excluded set, even if excluded values are also present; empty arrays and absent fields do not match.
    /// </summary>
    [Test]
    [DisplayName("events.test-event-pattern-anything-but-over-array-valued-field-admits-any-escaping-element")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    [KnownFidelityGap(FidelityGap.EventBridgeEventPatterns, "anything-but over an array field rejects [\"a\", \"blocked\"] because one element is excluded; AWS matches when any element escapes the exclusion")]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ev = ctx.Events;

        // --- single excluded value --------------------------------------------------------------
        var one = JsonNode.Parse("""{"detail": {"tags": [{"anything-but": "blocked"}]}}""")!;
        await Expect(ev, one, Event(ctx, detail: """{"tags": ["a", "b"]}"""),
            true, "no element equals the excluded value");
        await Expect(ev, one, Event(ctx, detail: """{"tags": ["blocked"]}"""),
            false, "the only element equals the excluded value");
        await Expect(ev, one, Event(ctx, detail: """{"tags": ["a", "blocked"]}"""),
            true, "one element escapes the excluded value even though the other carries it");
        await Expect(ev, one, Event(ctx, detail: """{"tags": []}"""),
            false, "an empty array has no element that can escape the exclusion");

        // --- a list of excluded values: every element of the array must escape EVERY excluded
        // value for the array as a whole to be excluded; one escaping element is enough to admit ----
        var many = JsonNode.Parse("""{"detail": {"tags": [{"anything-but": ["blocked", "banned"]}]}}""")!;
        await Expect(ev, many, Event(ctx, detail: """{"tags": ["a", "b"]}"""),
            true, "no element is in the excluded set");
        await Expect(ev, many, Event(ctx, detail: """{"tags": ["blocked"]}"""),
            false, "the only element is the first excluded value");
        await Expect(ev, many, Event(ctx, detail: """{"tags": ["banned"]}"""),
            false, "the only element is the second excluded value");
        await Expect(ev, many, Event(ctx, detail: """{"tags": ["blocked", "banned"]}"""),
            false, "every element is in the excluded set");
        await Expect(ev, many, Event(ctx, detail: """{"tags": ["a", "blocked", "banned"]}"""),
            true, "one escaping element admits the event even though both excluded values are also present");

        // --- a field the event does not carry at all cannot be admitted -------------------------
        await Expect(ev, one, Event(ctx, detail: """{"other": ["a", "b"]}"""),
            false, "anything-but over an absent field must not match");
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
