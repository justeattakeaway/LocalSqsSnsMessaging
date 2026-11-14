// Translated from behavioral/events/check_test_event_pattern_numeric_rejects_non_numeric_json_values_and_degenerate_ranges.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Text.Json;
using System.Text.Json.Nodes;
using Amazon.EventBridge;
using Amazon.EventBridge.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Events;

/// <summary>events.test-event-pattern-numeric-rejects-non-numeric-json-values-and-degenerate-ranges</summary>
[Category("AwsEvaluationSuite")]
public sealed class TestEventPatternNumericRejectsNonNumericJsonValuesAndDegenerateRanges
{
    /// <summary>
    /// `numeric` never matches a numeric-looking JSON string, and TestEventPattern refuses a
    /// reversed numeric range and `numeric` nested in `anything-but` with InvalidEventPatternException.
    /// </summary>
    [Test]
    [DisplayName("events.test-event-pattern-numeric-rejects-non-numeric-json-values-and-degenerate-ranges")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    [KnownFidelityGap(FidelityGap.EventBridgeEventPatterns, "TestEventPattern evaluates a reversed numeric range ([\">\", 100, \"<\", 50]) to Result=False instead of refusing it with InvalidEventPatternException 'Bottom must be less than top'")]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ev = ctx.Events;

        // --- numeric never matches a numeric-looking JSON string -------------------------------
        const string gt = """{"detail": {"amount": [{"numeric": [">", 100]}]}}""";
        await Expect(ev, gt, Event(ctx, detail: """{"amount": "150"}"""),
            false, "numeric must not coerce a JSON string, even one that looks numeric");
        await Expect(ev, gt, Event(ctx, detail: """{"amount": 150}"""),
            true, "control: the same bound against the actual JSON number matches");

        // --- a reversed/degenerate range is refused, not evaluated -----------------------------
        // EventBridge validates bound ordering when the pattern is parsed, so this never evaluates.
        const string reversedPattern = """{"detail": {"amount": [{"numeric": [">", 100, "<", 50]}]}}""";
        var (code, message) = await ErrorCodeAndMessage(() => ev.TestEventPatternAsync(new TestEventPatternRequest
        {
            EventPattern = reversedPattern, Event = PyDumps(Event(ctx, detail: """{"amount": 75}""")),
        }));
        Ensure(
            code == "InvalidEventPatternException",
            $"expected InvalidEventPatternException for a reversed numeric range, got '{code}'");
        var expectedMessage =
            "Event pattern is not valid. Reason: Bottom must be less than top\n" +
            $" at [Source: (String)\"{reversedPattern}\"; line: 1, column: 55]";
        Ensure(message == expectedMessage, $"expected message '{expectedMessage}', got '{message}'");

        // --- numeric has no nested anything-but form --------------------------------------------
        const string nested = """{"detail": {"amount": [{"anything-but": {"numeric": [">", 100]}}]}}""";
        var (code2, message2) = await ErrorCodeAndMessage(() => ev.TestEventPatternAsync(new TestEventPatternRequest
        {
            EventPattern = nested, Event = PyDumps(Event(ctx, detail: """{"amount": 50}""")),
        }));
        Ensure(
            code2 == "InvalidEventPatternException",
            $"expected InvalidEventPatternException nesting numeric inside anything-but, got '{code2}'");
        var expectedMessage2 =
            "Event pattern is not valid. Reason: Unsupported anything-but pattern: numeric\n" +
            $" at [Source: (String)\"{nested}\"; line: 1, column: 54]";
        Ensure(message2 == expectedMessage2, $"expected message '{expectedMessage2}', got '{message2}'");
    }

    /// <summary>Call fn() and return the ClientError's (Code, Message); an unexpected success raises.</summary>
    private static async Task<(string? Code, string? Message)> ErrorCodeAndMessage(Func<Task<TestEventPatternResponse>> fn)
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is null)
        {
            throw Fail($"expected the call to be rejected, but it SUCCEEDED and returned {{'Result': {result!.Result}}}");
        }

        return (error.Code, error.Message);
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
