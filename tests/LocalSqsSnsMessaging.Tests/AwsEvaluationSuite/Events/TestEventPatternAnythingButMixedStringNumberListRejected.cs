// Translated from behavioral/events/check_test_event_pattern_anything_but_mixed_string_number_list_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Text.Json;
using System.Text.Json.Nodes;
using Amazon.EventBridge;
using Amazon.EventBridge.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Events;

/// <summary>events.test-event-pattern-anything-but-mixed-string-number-list-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class TestEventPatternAnythingButMixedStringNumberListRejected
{
    /// <summary>
    /// An `anything-but` list mixing strings and numbers is refused with an exact message, while
    /// an all-string list and an all-number list are accepted and match correctly.
    /// </summary>
    [Test]
    [DisplayName("events.test-event-pattern-anything-but-mixed-string-number-list-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ev = ctx.Events;

        var mixed = JsonNode.Parse("""{"detail": {"state": [{"anything-but": ["initializing", 1]}]}}""");
        var body = PyDumps(mixed);
        var (code, message) = await Error(() =>
            ev.TestEventPatternAsync(new TestEventPatternRequest { EventPattern = body, Event = PyDumps(Event(ctx)) }));
        Ensure(
            code == "InvalidEventPatternException",
            "a mixed string/number anything-but list must be rejected: expected " +
            $"InvalidEventPatternException, got {code}\n  pattern = {body}");
        var expected = PatternMessage(body);
        Ensure(
            message == expected,
            $"TestEventPattern refused the mixed anything-but list with message '{message}', expected '{expected}'");

        var strings = JsonNode.Parse("""{"detail": {"state": [{"anything-but": ["initializing", "stopping"]}]}}""")!;
        await ExpectMatches(ev, strings, Event(ctx, detail: """{"state": "running"}"""),
            true, "a string outside an all-string anything-but list passes");
        await ExpectMatches(ev, strings, Event(ctx, detail: """{"state": "stopping"}"""),
            false, "a string inside an all-string anything-but list is excluded");

        var numbers = JsonNode.Parse("""{"detail": {"code": [{"anything-but": [400, 500]}]}}""")!;
        await ExpectMatches(ev, numbers, Event(ctx, detail: """{"code": 200}"""),
            true, "a number outside an all-number anything-but list passes");
        await ExpectMatches(ev, numbers, Event(ctx, detail: """{"code": 500}"""),
            false, "a number inside an all-number anything-but list is excluded");
    }

    /// <summary>A complete, well-formed EventBridge envelope, with any field overridable.</summary>
    private static JsonObject Event(FidelityContext ctx, string? detail = null)
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

        return @event;
    }

    /// <summary>Run `call` and return the (code, message) of its AWS error; a success raises.</summary>
    private static async Task<(string Code, string Message)> Error<T>(Func<Task<T>> call)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(call);
        if (error is not null)
        {
            return (error.Code ?? "<missing Code>", error.Message ?? "<missing Message>");
        }

        throw Fail($"expected the pattern to be refused; it SUCCEEDED: {Show(result)}");
    }

    /// <summary>The exact InvalidEventPatternException message for a mixed anything-but list.</summary>
    private static string PatternMessage(string body) =>
        "Event pattern is not valid. Reason: Inside anything but list, either all values are " +
        "number or string, mixed type is not supported\n" +
        $" at [Source: (String)\"{body}\"; line: 1, column: 59]";

    private static async Task ExpectMatches(AmazonEventBridgeClient ev, JsonNode pattern, JsonNode @event, bool want, string why)
    {
        var resp = await ev.TestEventPatternAsync(new TestEventPatternRequest { EventPattern = PyDumps(pattern), Event = PyDumps(@event) });
        var result = resp.Result;
        Ensure(result is not null, "TestEventPattern.Result must be a JSON boolean, got None (NoneType)");
        Ensure(
            result == want,
            $"{why}: expected Result={Py(want)}, got {Py(result.Value)}\n" +
            $"  pattern = {PyDumps(pattern)}\n" +
            $"  event   = {PyDumps(@event)}");
    }

    private static string Show(object? result) =>
        result is TestEventPatternResponse r ? $"{{'Result': {(r.Result is { } b ? Py(b) : "None")}}}" : $"{result}";

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
