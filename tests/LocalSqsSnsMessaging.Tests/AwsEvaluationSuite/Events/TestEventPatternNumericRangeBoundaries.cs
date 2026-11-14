// Translated from behavioral/events/check_test_event_pattern_numeric_range_boundaries.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Text.Json;
using System.Text.Json.Nodes;
using Amazon.EventBridge;
using Amazon.EventBridge.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Events;

/// <summary>events.test-event-pattern-numeric-range-boundaries</summary>
[Category("AwsEvaluationSuite")]
public sealed class TestEventPatternNumericRangeBoundaries
{
    /// <summary>
    /// `numeric` `&gt;`, `&gt;=`, `&lt;`, `&lt;=` and `=` are asserted just below, on and just above the bound;
    /// a two-sided range ANDs both halves, with negative/fractional operands; an absent field misses.
    /// </summary>
    [Test]
    [DisplayName("events.test-event-pattern-numeric-range-boundaries")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ev = ctx.Events;

        // --- strictly greater than ------------------------------------------------------------
        const string gt = """{"detail": {"amount": [{"numeric": [">", 100]}]}}""";
        await Expect(ev, gt, Event(ctx, detail: """{"amount": 101}"""), true, "> is satisfied above the bound");
        await Expect(ev, gt, Event(ctx, detail: """{"amount": 100}"""), false, "> must EXCLUDE the bound itself");
        await Expect(ev, gt, Event(ctx, detail: """{"amount": 99}"""), false, "> is unsatisfied below the bound");

        // --- greater than or equal ------------------------------------------------------------
        const string ge = """{"detail": {"amount": [{"numeric": [">=", 100]}]}}""";
        await Expect(ev, ge, Event(ctx, detail: """{"amount": 101}"""), true, ">= is satisfied above the bound");
        await Expect(ev, ge, Event(ctx, detail: """{"amount": 100}"""), true, ">= must INCLUDE the bound itself");
        await Expect(ev, ge, Event(ctx, detail: """{"amount": 99}"""), false, ">= is unsatisfied below the bound");

        // --- strictly less than / less than or equal ------------------------------------------
        const string lt = """{"detail": {"amount": [{"numeric": ["<", 100]}]}}""";
        await Expect(ev, lt, Event(ctx, detail: """{"amount": 99}"""), true, "< is satisfied below the bound");
        await Expect(ev, lt, Event(ctx, detail: """{"amount": 100}"""), false, "< must EXCLUDE the bound itself");
        await Expect(ev, lt, Event(ctx, detail: """{"amount": 101}"""), false, "< is unsatisfied above the bound");

        const string le = """{"detail": {"amount": [{"numeric": ["<=", 100]}]}}""";
        await Expect(ev, le, Event(ctx, detail: """{"amount": 99}"""), true, "<= is satisfied below the bound");
        await Expect(ev, le, Event(ctx, detail: """{"amount": 100}"""), true, "<= must INCLUDE the bound itself");
        await Expect(ev, le, Event(ctx, detail: """{"amount": 101}"""), false, "<= is unsatisfied above the bound");

        // --- numeric equality -----------------------------------------------------------------
        const string eq = """{"detail": {"amount": [{"numeric": ["=", 100]}]}}""";
        await Expect(ev, eq, Event(ctx, detail: """{"amount": 100}"""), true, "= matches the exact number");
        await Expect(ev, eq, Event(ctx, detail: """{"amount": 100.0}"""), true, "= matches the same value written 100.0");
        await Expect(ev, eq, Event(ctx, detail: """{"amount": 100.5}"""), false, "= must not match a nearby number");

        // --- two-sided range: both operator/operand pairs must hold (AND, not OR) -------------
        const string band = """{"detail": {"amount": [{"numeric": [">", 0, "<=", 100]}]}}""";
        await Expect(ev, band, Event(ctx, detail: """{"amount": 50}"""), true, "value inside the range");
        await Expect(ev, band, Event(ctx, detail: """{"amount": 100}"""), true, "upper bound is inclusive (<=)");
        await Expect(ev, band, Event(ctx, detail: """{"amount": 0}"""), false, "lower bound is exclusive (>)");
        await Expect(ev, band, Event(ctx, detail: """{"amount": 101}"""), false, "above the upper bound");
        await Expect(ev, band, Event(ctx, detail: """{"amount": -1}"""), false,
            "below the lower bound -- both halves of the range must hold, so this is not an OR");

        // --- negative and fractional operands -------------------------------------------------
        const string neg = """{"detail": {"delta": [{"numeric": [">=", -10, "<", -1.5]}]}}""";
        await Expect(ev, neg, Event(ctx, detail: """{"delta": -10}"""), true, "negative inclusive lower bound");
        await Expect(ev, neg, Event(ctx, detail: """{"delta": -1.75}"""), true, "fractional value inside the range");
        await Expect(ev, neg, Event(ctx, detail: """{"delta": -1.5}"""), false, "fractional exclusive upper bound");
        await Expect(ev, neg, Event(ctx, detail: """{"delta": 0}"""), false, "zero is outside a negative range");
        await Expect(ev, neg, Event(ctx, detail: """{"delta": -10.0001}"""), false, "just below the lower bound");

        // --- an absent field cannot satisfy a range -------------------------------------------
        await Expect(ev, gt, Event(ctx, detail: """{"other": 500}"""), false,
            "a numeric range over a field the event does not carry must not match");
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
