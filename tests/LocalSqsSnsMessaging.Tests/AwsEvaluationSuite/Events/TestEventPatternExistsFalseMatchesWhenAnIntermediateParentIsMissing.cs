// Translated from behavioral/events/check_test_event_pattern_exists_false_matches_when_an_intermediate_parent_is_missing.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Text.Json;
using System.Text.Json.Nodes;
using Amazon.EventBridge;
using Amazon.EventBridge.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Events;

/// <summary>events.test-event-pattern-exists-false-matches-when-an-intermediate-parent-is-missing</summary>
[Category("AwsEvaluationSuite")]
public sealed class TestEventPatternExistsFalseMatchesWhenAnIntermediateParentIsMissing
{
    /// <summary>
    /// `exists: false` on a nested leaf matches whether the leaf or its parent object is missing, and
    /// `exists: true` needs the whole path present; either composes by AND with a sibling key.
    /// </summary>
    [Test]
    [DisplayName("events.test-event-pattern-exists-false-matches-when-an-intermediate-parent-is-missing")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    [KnownFidelityGap(FidelityGap.EventBridgeEventPatterns, "exists:false on detail.order.customer does not match when the parent object 'order' is missing (Result=False instead of True)")]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ev = ctx.Events;

        const string leafAbsent = """{"detail": {"order": {"customer": [{"exists": false}]}}}""";

        await Expect(ev, leafAbsent, Event(ctx, detail: "{}"),
            true, "exists:false matches when the whole PARENT object 'order' is missing");
        await Expect(ev, leafAbsent, Event(ctx, detail: """{"order": {"other": 1}}"""),
            true, "exists:false matches when 'order' is present but the leaf 'customer' is missing");
        await Expect(ev, leafAbsent, Event(ctx, detail: """{"order": {"customer": "C1"}}"""),
            false, "exists:false must not match when the leaf 'customer' is present");

        // The same distinction, inverted with exists:true.
        const string leafPresent = """{"detail": {"order": {"customer": [{"exists": true}]}}}""";
        await Expect(ev, leafPresent, Event(ctx, detail: "{}"),
            false, "exists:true must not match when the parent 'order' is missing");
        await Expect(ev, leafPresent, Event(ctx, detail: """{"order": {"other": 1}}"""),
            false, "exists:true must not match when the parent is present but the leaf is missing");
        await Expect(ev, leafPresent, Event(ctx, detail: """{"order": {"customer": "C1"}}"""),
            true, "exists:true must match when the whole path down to the leaf is present");

        // exists:false composes by AND with a sibling value key at the SAME nesting level as the
        // missing parent, so the whole pattern still fails if that sibling is unsatisfied.
        const string composed = """{"source": ["sim2real.orders"], "detail": {"order": {"customer": [{"exists": false}]}}}""";
        await Expect(ev, composed, Event(ctx, source: "sim2real.orders", detail: "{}"),
            true, "both the source key and the missing-parent exists:false are satisfied");
        await Expect(ev, composed, Event(ctx, source: "sim2real.payments", detail: "{}"),
            false, "the source key alone unsatisfied fails the whole pattern");
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
