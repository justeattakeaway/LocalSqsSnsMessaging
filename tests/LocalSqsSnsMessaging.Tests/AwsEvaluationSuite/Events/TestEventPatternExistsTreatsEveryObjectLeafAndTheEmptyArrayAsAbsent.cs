// Translated from behavioral/events/check_test_event_pattern_exists_treats_every_object_leaf_and_the_empty_array_as_absent.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Text.Json;
using System.Text.Json.Nodes;
using Amazon.EventBridge;
using Amazon.EventBridge.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Events;

/// <summary>events.test-event-pattern-exists-treats-every-object-leaf-and-the-empty-array-as-absent</summary>
[Category("AwsEvaluationSuite")]
public sealed class TestEventPatternExistsTreatsEveryObjectLeafAndTheEmptyArrayAsAbsent
{
    /// <summary>
    /// `exists` treats any JSON object leaf (empty or not) and an empty array as absent, while a
    /// non-empty array and null are present; `exists: false` is the complement throughout.
    /// </summary>
    [Test]
    [DisplayName("events.test-event-pattern-exists-treats-every-object-leaf-and-the-empty-array-as-absent")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ev = ctx.Events;

        const string existsTrue = """{"detail": {"items": [{"exists": true}]}}""";
        const string existsFalse = """{"detail": {"items": [{"exists": false}]}}""";

        // --- a JSON object leaf is absent regardless of whether it is empty --------------------
        await Expect(ev, existsTrue, Event(ctx, detail: """{"items": {}}"""),
            false, "exists:true must not match an empty-object leaf");
        await Expect(ev, existsFalse, Event(ctx, detail: """{"items": {}}"""),
            true, "exists:false must match an empty-object leaf, like a truly absent field");
        await Expect(ev, existsTrue, Event(ctx, detail: """{"items": {"k": "v", "k2": 2}}"""),
            false, "exists:true must not match a NON-empty-object leaf either");
        await Expect(ev, existsFalse, Event(ctx, detail: """{"items": {"k": "v", "k2": 2}}"""),
            true, "exists:false must match a non-empty-object leaf too");

        // --- a JSON array leaf depends on emptiness, unlike an object --------------------------
        await Expect(ev, existsTrue, Event(ctx, detail: """{"items": []}"""),
            false, "exists:true must not match an empty-array leaf");
        await Expect(ev, existsFalse, Event(ctx, detail: """{"items": []}"""),
            true, "exists:false must match an empty-array leaf, like a truly absent field");
        await Expect(ev, existsTrue, Event(ctx, detail: """{"items": ["a"]}"""),
            true, "exists:true MUST match a non-empty-array leaf -- unlike the object case");
        await Expect(ev, existsFalse, Event(ctx, detail: """{"items": ["a"]}"""),
            false, "exists:false must not match a non-empty-array leaf");

        // --- JSON null stays on the present side, like a non-empty array -----------------------
        await Expect(ev, existsTrue, Event(ctx, detail: """{"items": null}"""),
            true, "exists:true must match a null leaf -- null is present, unlike {} or []");
        await Expect(ev, existsFalse, Event(ctx, detail: """{"items": null}"""),
            false, "exists:false must not match a null leaf");

        // --- truly absent agrees with {} and [], for the control --------------------------------
        await Expect(ev, existsTrue, Event(ctx, detail: "{}"),
            false, "control: exists:true must not match a truly absent field");
        await Expect(ev, existsFalse, Event(ctx, detail: "{}"),
            true, "control: exists:false must match a truly absent field");
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
