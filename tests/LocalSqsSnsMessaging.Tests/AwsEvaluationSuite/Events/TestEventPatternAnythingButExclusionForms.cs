// Translated from behavioral/events/check_test_event_pattern_anything_but_exclusion_forms.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Text.Json;
using System.Text.Json.Nodes;
using Amazon.EventBridge;
using Amazon.EventBridge.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Events;

/// <summary>events.test-event-pattern-anything-but-exclusion-forms</summary>
[Category("AwsEvaluationSuite")]
public sealed class TestEventPatternAnythingButExclusionForms
{
    /// <summary>
    /// `anything-but` excludes a single string, every element of a string or number list, and a
    /// nested `prefix` namespace, while still admitting values outside the excluded set.
    /// </summary>
    [Test]
    [DisplayName("events.test-event-pattern-anything-but-exclusion-forms")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ev = ctx.Events;

        // --- single string operand ------------------------------------------------------------
        var one = JsonNode.Parse("""{"detail": {"state": [{"anything-but": "initializing"}]}}""")!;
        await Expect(ev, one, Event(ctx, detail: """{"state": "running"}"""),
            true, "anything-but admits a value other than the excluded one");
        await Expect(ev, one, Event(ctx, detail: """{"state": "initializing"}"""),
            false, "anything-but must exclude the operand itself");
        // The comparison it negates is the byte-exact one, so case matters here too.
        await Expect(ev, one, Event(ctx, detail: """{"state": "Initializing"}"""),
            true, "anything-but negates a case-SENSITIVE comparison, so a recased value passes");
        // And it is whole-value, not substring: a longer value containing the operand still passes.
        await Expect(ev, one, Event(ctx, detail: """{"state": "initializing-slowly"}"""),
            true, "anything-but excludes the whole value, not any value containing it");

        // --- list of strings: EVERY element is excluded ----------------------------------------
        var many = JsonNode.Parse("""{"detail": {"state": [{"anything-but": ["initializing", "stopping", "failed"]}]}}""")!;
        await Expect(ev, many, Event(ctx, detail: """{"state": "running"}"""),
            true, "a value in none of the excluded values passes");
        await Expect(ev, many, Event(ctx, detail: """{"state": "initializing"}"""),
            false, "the FIRST excluded value must be excluded");
        await Expect(ev, many, Event(ctx, detail: """{"state": "stopping"}"""),
            false, "a MIDDLE excluded value must be excluded");
        await Expect(ev, many, Event(ctx, detail: """{"state": "failed"}"""),
            false, "the LAST excluded value must be excluded");

        // --- list of numbers ------------------------------------------------------------------
        var nums = JsonNode.Parse("""{"detail": {"code": [{"anything-but": [400, 500]}]}}""")!;
        await Expect(ev, nums, Event(ctx, detail: """{"code": 200}"""),
            true, "a number outside the excluded set passes");
        await Expect(ev, nums, Event(ctx, detail: """{"code": 400}"""),
            false, "the first excluded number must be excluded");
        await Expect(ev, nums, Event(ctx, detail: """{"code": 500}"""),
            false, "the second excluded number must be excluded");
        await Expect(ev, nums, Event(ctx, detail: """{"code": 500.0}"""),
            false, "500.0 is the same JSON number as 500, so it is excluded too");

        // --- nested prefix: excludes an entire namespace, not one literal ----------------------
        var pre = JsonNode.Parse("""{"source": [{"anything-but": {"prefix": "sim2real.internal."}}]}""")!;
        await Expect(ev, pre, Event(ctx, source: "sim2real.orders"),
            true, "a source outside the excluded prefix passes");
        await Expect(ev, pre, Event(ctx, source: "sim2real.internal.audit"),
            false, "a source under the excluded prefix must be excluded");
        await Expect(ev, pre, Event(ctx, source: "sim2real.internal."),
            false, "a source equal to the excluded prefix must be excluded");
        await Expect(ev, pre, Event(ctx, source: "x.sim2real.internal.audit"),
            true, "the excluded prefix is anchored at the start, so this is not excluded");
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
