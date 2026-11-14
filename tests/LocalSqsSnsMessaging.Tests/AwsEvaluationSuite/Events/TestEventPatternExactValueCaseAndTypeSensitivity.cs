// Translated from behavioral/events/check_test_event_pattern_exact_value_case_and_type_sensitivity.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Text.Json;
using System.Text.Json.Nodes;
using Amazon.EventBridge;
using Amazon.EventBridge.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Events;

/// <summary>events.test-event-pattern-exact-value-case-and-type-sensitivity</summary>
[Category("AwsEvaluationSuite")]
public sealed class TestEventPatternExactValueCaseAndTypeSensitivity
{
    /// <summary>
    /// Exact-value matching is case-sensitive and JSON-type-sensitive (5 vs "5", true vs "true"),
    /// treats 5 and 5.0 as one number, and requires nested pattern paths to match the event's nesting.
    /// </summary>
    [Test]
    [DisplayName("events.test-event-pattern-exact-value-case-and-type-sensitivity")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ev = ctx.Events;

        // --- case sensitivity on a top-level string field -------------------------------------
        await Expect(ev, """{"source": ["sim2real.Orders"]}""", Event(ctx, source: "sim2real.Orders"),
            true, "exact source match on identical bytes");
        await Expect(ev, """{"source": ["sim2real.Orders"]}""", Event(ctx, source: "sim2real.orders"),
            false, "source match must be case-sensitive");
        await Expect(ev, """{"detail-type": ["OrderPlaced"]}""", Event(ctx, detailType: "OrderPlaced"),
            true, "exact detail-type match");
        await Expect(ev, """{"detail-type": ["OrderPlaced"]}""", Event(ctx, detailType: "orderplaced"),
            false, "detail-type match must be case-sensitive");

        // --- JSON type sensitivity inside detail ----------------------------------------------
        await Expect(ev, """{"detail": {"qty": [5]}}""", Event(ctx, detail: """{"qty": 5}"""),
            true, "number pattern matches the same number");
        await Expect(ev, """{"detail": {"qty": [5]}}""", Event(ctx, detail: """{"qty": "5"}"""),
            false, "number pattern must NOT match the string \"5\"");
        await Expect(ev, """{"detail": {"qty": ["5"]}}""", Event(ctx, detail: """{"qty": "5"}"""),
            true, "string pattern matches the same string");
        await Expect(ev, """{"detail": {"qty": ["5"]}}""", Event(ctx, detail: """{"qty": 5}"""),
            false, "string pattern must NOT match the number 5");

        // --- boolean vs the string spelling of a boolean --------------------------------------
        await Expect(ev, """{"detail": {"paid": [true]}}""", Event(ctx, detail: """{"paid": true}"""),
            true, "boolean pattern matches the same boolean");
        await Expect(ev, """{"detail": {"paid": [true]}}""", Event(ctx, detail: """{"paid": "true"}"""),
            false, "boolean pattern must NOT match the string \"true\"");

        // --- numeric equality is on VALUE, so 5 and 5.0 are the same JSON number -------------
        await Expect(ev, """{"detail": {"qty": [5]}}""", Event(ctx, detail: """{"qty": 5.0}"""),
            true, "5 and 5.0 are the same JSON number");

        // --- nested paths must line up with the event's own structure -------------------------
        await Expect(ev, """{"detail": {"order": {"id": ["A1"]}}}""",
            Event(ctx, detail: """{"order": {"id": "A1"}}"""),
            true, "nested pattern path matches the nested event path");
        await Expect(ev, """{"detail": {"order": {"id": ["A1"]}}}""",
            Event(ctx, detail: """{"id": "A1"}"""),
            false, "a nested pattern path must not match a flattened event");
        await Expect(ev, """{"detail": {"id": ["A1"]}}""",
            Event(ctx, detail: """{"order": {"id": "A1"}}"""),
            false, "a flat pattern path must not match a nested event");
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
