// Translated from behavioral/events/check_test_event_pattern_multiple_filters_in_one_list_are_or_joined.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Text.Json;
using System.Text.Json.Nodes;
using Amazon.EventBridge;
using Amazon.EventBridge.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Events;

/// <summary>events.test-event-pattern-multiple-filters-in-one-list-are-or-joined</summary>
[Category("AwsEvaluationSuite")]
public sealed class TestEventPatternMultipleFiltersInOneListAreOrJoined
{
    /// <summary>
    /// A key's list can mix different operators, or a literal with an operator, and the key matches
    /// when any element matches, so a value only the second filter matches is still admitted.
    /// </summary>
    [Test]
    [DisplayName("events.test-event-pattern-multiple-filters-in-one-list-are-or-joined")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ev = ctx.Events;

        // --- a string-comparator and a numeric-comparator in the same list, OR-joined -----------
        const string mixedOps = """{"detail": {"state": [{"prefix": "ready"}, {"numeric": [">", 100]}]}}""";
        await Expect(ev, mixedOps, Event(ctx, detail: """{"state": "readyish"}"""),
            true, "the FIRST filter (prefix) matches");
        await Expect(ev, mixedOps, Event(ctx, detail: """{"state": 150}"""),
            true, "the SECOND filter (numeric) matches a value the first filter cannot even see");
        await Expect(ev, mixedOps, Event(ctx, detail: """{"state": "other"}"""),
            false, "neither filter matches");
        await Expect(ev, mixedOps, Event(ctx, detail: """{"state": "150"}"""),
            false, "the JSON string \"150\" matches neither: not a numeric value, " +
            "and it does not start with the prefix");

        // --- a plain literal value mixed with an operator object in the same list ---------------
        const string mixedLiteral = """{"detail": {"tier": ["bronze", {"prefix": "go"}]}}""";
        await Expect(ev, mixedLiteral, Event(ctx, detail: """{"tier": "bronze"}"""),
            true, "the literal alternative matches");
        await Expect(ev, mixedLiteral, Event(ctx, detail: """{"tier": "gold"}"""),
            true, "the operator alternative matches a value the literal cannot");
        await Expect(ev, mixedLiteral, Event(ctx, detail: """{"tier": "silver"}"""),
            false, "neither alternative matches");
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
