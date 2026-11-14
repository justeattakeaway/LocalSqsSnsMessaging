// Translated from behavioral/events/check_test_event_pattern_match.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Text.Json;
using System.Text.Json.Nodes;
using Amazon.EventBridge.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Events;

/// <summary>events.test-event-pattern-match</summary>
[Category("AwsEvaluationSuite")]
public sealed class TestEventPatternMatch
{
    /// <summary>
    /// TestEventPattern reports a match when the event satisfies the pattern,
    /// and reports no match for an otherwise-identical event that does not.
    /// </summary>
    [Test]
    [DisplayName("events.test-event-pattern-match")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ev = ctx.Events;
        const string pattern = """{"source": ["test.source"]}""";

        string Envelope(string source) => PyDumps(new JsonObject
        {
            ["source"] = source,
            ["detail-type"] = "test",
            ["detail"] = new JsonObject(),
            ["region"] = ctx.Region,
            ["account"] = "123456789012",
            ["id"] = "11111111-1111-1111-1111-111111111111",
            ["time"] = "2023-01-01T00:00:00Z",
            ["resources"] = new JsonArray(),
        });

        var matchResp = await ev.TestEventPatternAsync(new TestEventPatternRequest { EventPattern = pattern, Event = Envelope("test.source") });
        Ensure(
            matchResp.Result is true,
            $"expected Result=True for an event matching the pattern, got {{'Result': {matchResp.Result}}}");

        var mismatchResp = await ev.TestEventPatternAsync(new TestEventPatternRequest { EventPattern = pattern, Event = Envelope("other.source") });
        Ensure(
            mismatchResp.Result is false,
            $"expected Result=False for an event that does not match the pattern, got {{'Result': {mismatchResp.Result}}}");
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
