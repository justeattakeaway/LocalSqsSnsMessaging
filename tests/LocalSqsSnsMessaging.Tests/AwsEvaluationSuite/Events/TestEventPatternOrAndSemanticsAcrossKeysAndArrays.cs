// Translated from behavioral/events/check_test_event_pattern_or_and_semantics_across_keys_and_arrays.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Text.Json;
using System.Text.Json.Nodes;
using Amazon.EventBridge;
using Amazon.EventBridge.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Events;

/// <summary>events.test-event-pattern-or-and-semantics-across-keys-and-arrays</summary>
[Category("AwsEvaluationSuite")]
public sealed class TestEventPatternOrAndSemanticsAcrossKeysAndArrays
{
    /// <summary>
    /// A value list under one key is OR, multiple keys are AND (an absent key fails), a pattern
    /// value matches any element of an array field, and top-level `$or` expresses OR across keys.
    /// </summary>
    [Test]
    [DisplayName("events.test-event-pattern-or-and-semantics-across-keys-and-arrays")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ev = ctx.Events;

        // --- (1) a list under one key is OR ---------------------------------------------------
        const string twoSources = """{"source": ["sim2real.orders", "sim2real.payments"]}""";
        await Expect(ev, twoSources, Event(ctx, source: "sim2real.orders"),
            true, "first alternative in an OR list matches");
        await Expect(ev, twoSources, Event(ctx, source: "sim2real.payments"),
            true, "second alternative in an OR list matches");
        await Expect(ev, twoSources, Event(ctx, source: "sim2real.shipping"),
            false, "a value in neither alternative must not match");

        // --- (2) multiple keys are AND --------------------------------------------------------
        const string both = """{"source": ["sim2real.orders"], "detail-type": ["OrderPlaced"]}""";
        await Expect(ev, both, Event(ctx, source: "sim2real.orders", detailType: "OrderPlaced"),
            true, "both keys satisfied");
        await Expect(ev, both, Event(ctx, source: "sim2real.orders", detailType: "OrderCancelled"),
            false, "second key unsatisfied must fail the whole pattern (AND, not OR)");
        await Expect(ev, both, Event(ctx, source: "sim2real.payments", detailType: "OrderPlaced"),
            false, "first key unsatisfied must fail the whole pattern (AND, not OR)");

        // AND also holds between sibling keys inside `detail`.
        const string twoDetailKeys = """{"detail": {"state": ["PROD"], "tier": ["gold"]}}""";
        await Expect(ev, twoDetailKeys, Event(ctx, detail: """{"state": "PROD", "tier": "gold"}"""),
            true, "both detail keys satisfied");
        await Expect(ev, twoDetailKeys, Event(ctx, detail: """{"state": "PROD", "tier": "silver"}"""),
            false, "sibling detail keys are AND");

        // A pattern key the event does not carry at all fails -- absence is not a wildcard.
        await Expect(ev, both, Event(ctx, source: "sim2real.orders", detailType: "OrderPlaced"),
            true, "control for the absent-key case");
        const string absentKey = """{"source": ["sim2real.orders"], "detail": {"tier": ["gold"]}}""";
        await Expect(ev, absentKey, Event(ctx, source: "sim2real.orders", detail: "{}"),
            false, "a pattern key absent from the event must not match");

        // --- (3) a scalar pattern value against an ARRAY-valued event field -------------------
        var arn = $"arn:aws:events:{ctx.Region}:123456789012:rule/sim2real-behav-any-element";
        var other = $"arn:aws:events:{ctx.Region}:123456789012:rule/sim2real-behav-other";
        await Expect(ev, $$"""{"resources": ["{{arn}}"]}""", Event(ctx, resources: $$"""["{{other}}", "{{arn}}"]"""),
            true, "a pattern value matching ANY element of the event's array matches");
        await Expect(ev, $$"""{"resources": ["{{arn}}"]}""", Event(ctx, resources: $$"""["{{other}}"]"""),
            false, "a pattern value matching NO element of the event's array must not match");
        await Expect(ev, $$"""{"resources": ["{{arn}}"]}""", Event(ctx, resources: "[]"),
            false, "an empty event array cannot satisfy a value pattern");
        // Both sides lists: matching is "some element of each side agrees".
        await Expect(ev, """{"detail": {"tags": ["blue", "green"]}}""",
            Event(ctx, detail: """{"tags": ["red", "green"]}"""),
            true, "OR list vs event array matches on the single shared element");
        await Expect(ev, """{"detail": {"tags": ["blue", "green"]}}""",
            Event(ctx, detail: """{"tags": ["red", "yellow"]}"""),
            false, "OR list vs event array with no shared element must not match");

        // --- (4) top-level $or is the only cross-key disjunction ------------------------------
        const string disjunction = """{"$or": [{"source": ["sim2real.orders"]}, {"detail-type": ["Emergency"]}]}""";
        await Expect(ev, disjunction, Event(ctx, source: "sim2real.orders", detailType: "OrderPlaced"),
            true, "$or satisfied by its first branch");
        await Expect(ev, disjunction, Event(ctx, source: "sim2real.payments", detailType: "Emergency"),
            true, "$or satisfied by its second branch");
        await Expect(ev, disjunction, Event(ctx, source: "sim2real.payments", detailType: "OrderPlaced"),
            false, "$or with neither branch satisfied must not match");
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
