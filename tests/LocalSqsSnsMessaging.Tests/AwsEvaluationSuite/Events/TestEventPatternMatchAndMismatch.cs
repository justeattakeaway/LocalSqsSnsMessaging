// Translated from behavioral/events/check_test_event_pattern_match_and_mismatch.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Text.Json;
using System.Text.Json.Nodes;
using Amazon.EventBridge.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Events;

/// <summary>events.test-event-pattern-match-and-mismatch</summary>
[Category("AwsEvaluationSuite")]
public sealed class TestEventPatternMatchAndMismatch
{
    /// <summary>
    /// TestEventPattern returns Result=True for a matching event and False for a non-matching one;
    /// an incomplete event envelope is rejected with ValidationException.
    /// </summary>
    [Test]
    [DisplayName("events.test-event-pattern-match-and-mismatch")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    [KnownFidelityGap(FidelityGap.EventBridgeValidation, "TestEventPattern accepts an event envelope missing id, detail-type, account, time and region (Result=True) instead of refusing it with ValidationException 'Parameter Event is not valid.'")]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ev = ctx.Events;
        var busArn = (await ev.DescribeEventBusAsync(new DescribeEventBusRequest { Name = "default" })).Arn;
        var parts = busArn.Split(':', 6);
        var (region, account) = (parts[3], parts[4]);
        const string pattern = """{"source": ["evb.match.source"]}""";

        string Envelope(string source) => PyDumps(new JsonObject
        {
            ["id"] = "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee",
            ["detail-type"] = "BehavioralCheck",
            ["source"] = source,
            ["account"] = account,
            ["time"] = "2024-01-01T00:00:00Z",
            ["region"] = region,
            ["resources"] = new JsonArray(),
            ["detail"] = new JsonObject { ["k"] = "v" },
        });

        var matchResp = await ev.TestEventPatternAsync(new TestEventPatternRequest { EventPattern = pattern, Event = Envelope("evb.match.source") });
        Ensure(matchResp.Result is true, $"expected Result=True, got {{'Result': {matchResp.Result}}}");

        var mismatchResp = await ev.TestEventPatternAsync(new TestEventPatternRequest { EventPattern = pattern, Event = Envelope("evb.other.source") });
        Ensure(mismatchResp.Result is false, $"expected Result=False, got {{'Result': {mismatchResp.Result}}}");

        // An incomplete envelope is a validation error, not a silent False.
        var (code, message) = await Error(() => ev.TestEventPatternAsync(new TestEventPatternRequest
        {
            EventPattern = pattern,
            Event = """{"source": "evb.match.source", "detail": {}}""",
        }));
        Ensure(
            code == "ValidationException",
            $"expected ValidationException for an incomplete event envelope, got {code}");
        Ensure(
            message == "Parameter Event is not valid.",
            $"incomplete event envelope refused with message '{message}', " +
            "expected 'Parameter Event is not valid.'");
    }

    /// <summary>Call fn() and return (Code, Message) of the ClientError it raised; an unexpected success raises.</summary>
    private static async Task<(string? Code, string? Message)> Error(Func<Task<TestEventPatternResponse>> fn)
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is null)
        {
            throw Fail($"expected the incomplete envelope to be rejected, but it SUCCEEDED and returned {{'Result': {result!.Result}}}");
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
