// Translated from behavioral/events/check_test_event_pattern_invalid_fails.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Text.Json;
using System.Text.Json.Nodes;
using Amazon.EventBridge.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Events;

/// <summary>events.test-event-pattern-invalid-fails</summary>
[Category("AwsEvaluationSuite")]
public sealed class TestEventPatternInvalidFails
{
    /// <summary>
    /// TestEventPattern matches a well-formed pattern, and rejects unparseable JSON, a scalar filter value
    /// and an empty filter array, each with InvalidEventPatternException and its exact message.
    /// </summary>
    [Test]
    [DisplayName("events.test-event-pattern-invalid-fails")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ev = ctx.Events;
        var @event = PyDumps(new JsonObject
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
        });

        var control = await ev.TestEventPatternAsync(new TestEventPatternRequest
        {
            EventPattern = """{"source": ["sim2real.orders"]}""", Event = @event,
        });
        Ensure(
            control.Result is true,
            "positive control failed: a well-formed matching pattern must report Result=True, " +
            $"got {{'Result': {control.Result}}}");

        const string badJson = "{not valid json";
        const string scalar = """{"source": "sim2real.orders"}""";
        const string empty = """{"source": []}""";
        (string Pattern, string Why, string ExpectedMessage)[] cases =
        [
            (
                badJson,
                "JSON text that does not parse",
                "Event pattern is not valid. Reason: Unexpected character ('n' (code 110)): was " +
                "expecting double-quote to start field name\n" +
                $" at [Source: (String)\"{badJson}\"; line: 1, column: 2]"
            ),
            (
                scalar,
                "a filter value that is a bare scalar, not an array",
                "Event pattern is not valid. Reason: \"source\" must be an object or an array\n" +
                $" at [Source: (String)\"{scalar}\"; line: 1, column: 13]"
            ),
            (
                empty,
                "a filter value that is an empty array",
                "Event pattern is not valid. Reason: Empty arrays are not allowed\n" +
                $" at [Source: (String)\"{empty}\"; line: 1, column: 14]"
            ),
        ];
        foreach (var (pattern, why, expectedMessage) in cases)
        {
            var err = await ClientError(() => ev.TestEventPatternAsync(new TestEventPatternRequest { EventPattern = pattern, Event = @event }));
            Ensure(
                err.Code == "InvalidEventPatternException",
                $"TestEventPattern must reject {why}: expected InvalidEventPatternException, got {err}\n" +
                $"  pattern = {pattern}");
            Ensure(
                err.Message == expectedMessage,
                $"TestEventPattern must reject {why} with message '{expectedMessage}', got " +
                $"'{err.Message}'");
        }
    }

    /// <summary>Run <paramref name="call"/> and return the botocore Error dict it raised. An unexpected SUCCESS raises.</summary>
    private static async Task<AwsError> ClientError(Func<Task<TestEventPatternResponse>> call)
    {
        var (result, error) = await FidelityContext.Capture(call);
        return error ?? throw Fail($"expected this call to be rejected, but it SUCCEEDED and returned {{'Result': {result!.Result}}}");
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
