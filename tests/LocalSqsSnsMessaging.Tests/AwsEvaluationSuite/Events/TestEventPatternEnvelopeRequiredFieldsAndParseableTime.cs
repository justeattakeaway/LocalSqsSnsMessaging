// Translated from behavioral/events/check_test_event_pattern_envelope_required_fields_and_parseable_time.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Text.Json;
using System.Text.Json.Nodes;
using Amazon.EventBridge.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Events;

/// <summary>events.test-event-pattern-envelope-required-fields-and-parseable-time</summary>
[Category("AwsEvaluationSuite")]
public sealed class TestEventPatternEnvelopeRequiredFieldsAndParseableTime
{
    private static readonly string[] Required = ["id", "detail-type", "source", "account", "time", "region"];

    /// <summary>
    /// TestEventPattern refuses an Event missing any of six envelope fields, an unparseable time or
    /// non-JSON with ValidationException and exact messages; missing resources or detail still matches.
    /// </summary>
    [Test]
    [DisplayName("events.test-event-pattern-envelope-required-fields-and-parseable-time")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    [KnownFidelityGap(FidelityGap.EventBridgeValidation, "TestEventPattern accepts an event envelope without 'id' (Result=True) instead of refusing it with ValidationException 'Parameter Event is not valid.'")]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ev = ctx.Events;
        const string pattern = """{"source": ["sim2real.envelope"]}""";
        JsonObject Full() => new()
        {
            ["id"] = "00000000-0000-0000-0000-000000000001",
            ["detail-type"] = "Sim2realEnvelope",
            ["source"] = "sim2real.envelope",
            ["account"] = ctx.Account,
            ["time"] = "2024-01-01T00:00:00Z",
            ["region"] = ctx.Region,
            ["resources"] = new JsonArray(),
            ["detail"] = new JsonObject { ["k"] = "v" },
        };

        var control = await ev.TestEventPatternAsync(new TestEventPatternRequest { EventPattern = pattern, Event = PyDumps(Full()) });
        Ensure(control.Result is true, $"the complete envelope did not match its source pattern: {{'Result': {control.Result}}}");

        foreach (var field in Required)
        {
            var @event = Full();
            @event.Remove(field);
            var (code, message) = await Error(() => ev.TestEventPatternAsync(new TestEventPatternRequest { EventPattern = pattern, Event = PyDumps(@event) }));
            Ensure(code == "ValidationException", $"an envelope without '{field}' gave '{code}'");
            Ensure(message == "Parameter Event is not valid.", $"an envelope without '{field}' was refused with '{message}'");
        }

        foreach (var field in new[] { "resources", "detail" })
        {
            var @event = Full();
            @event.Remove(field);
            var resp = await ev.TestEventPatternAsync(new TestEventPatternRequest { EventPattern = pattern, Event = PyDumps(@event) });
            Ensure(resp.Result is true, $"an envelope without optional '{field}' did not match: {{'Result': {resp.Result}}}");
        }

        var badTime = Full();
        badTime["time"] = "sim2real-not-a-timestamp";
        var (timeCode, timeMessage) = await Error(() => ev.TestEventPatternAsync(new TestEventPatternRequest { EventPattern = pattern, Event = PyDumps(badTime) }));
        Ensure(timeCode == "ValidationException", $"an envelope with an unparseable time gave '{timeCode}'");
        Ensure(
            timeMessage == "Parameter Event is not valid. Reason: Invalid time.",
            $"an envelope with an unparseable time was refused with '{timeMessage}'");

        var (jsonCode, jsonMessage) = await Error(() => ev.TestEventPatternAsync(new TestEventPatternRequest { EventPattern = pattern, Event = "{not json" }));
        Ensure(jsonCode == "ValidationException", $"an Event that is not JSON gave '{jsonCode}'");
        Ensure(jsonMessage == "Parameter Event is not valid.", $"an Event that is not JSON was refused with '{jsonMessage}'");
    }

    /// <summary>The (Error.Code, Error.Message) <paramref name="fn"/> raises. An unexpected success raises too.</summary>
    private static async Task<(string? Code, string? Message)> Error(Func<Task<TestEventPatternResponse>> fn)
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is null)
        {
            throw Fail($"expected the call to be refused, but it succeeded: {{'Result': {result!.Result}}}");
        }

        return (error.Code, error.Message);
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
