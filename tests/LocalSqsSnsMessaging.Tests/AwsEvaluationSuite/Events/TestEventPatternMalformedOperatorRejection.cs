// Translated from behavioral/events/check_test_event_pattern_malformed_operator_rejection.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Text.Json;
using System.Text.Json.Nodes;
using Amazon.EventBridge;
using Amazon.EventBridge.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Events;

/// <summary>events.test-event-pattern-malformed-operator-rejection</summary>
[Category("AwsEvaluationSuite")]
public sealed class TestEventPatternMalformedOperatorRejection
{
    /// <summary>
    /// Malformed event patterns are refused by TestEventPattern and PutRule with exact messages,
    /// each minimally-corrected form is accepted, and a refused PutRule leaves the stored rule intact.
    /// </summary>
    [Test]
    [DisplayName("events.test-event-pattern-malformed-operator-rejection")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    [KnownFidelityGap(FidelityGap.EventBridgeEventPatterns, "TestEventPattern evaluates {\"source\": \"sim2real.orders\"} (a leaf value not wrapped in an array) to Result=False instead of refusing it with InvalidEventPatternException '\"source\" must be an object or an array'")]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ev = ctx.Events;

        // Leaf values must be wrapped in an array. A bare scalar is not a one-element shorthand.
        await ExpectPatternInvalid(ev, """{"source": "sim2real.orders"}""",
            "a leaf value not wrapped in an array",
            "\"source\" must be an object or an array", 13);
        await ExpectPatternValid(ev, """{"source": ["sim2real.orders"]}""",
            Event(ctx), true, "the array-wrapped correction is accepted and matches");

        // An operator key the language does not define cannot be silently ignored.
        await ExpectPatternInvalid(ev, """{"source": [{"starts-with": "sim2real."}]}""",
            "an operator key that does not exist in the language",
            "Unrecognized match type starts-with", 30);
        await ExpectPatternValid(ev, """{"source": [{"prefix": "sim2real."}]}""",
            Event(ctx), true, "the real operator name is accepted and matches");

        // `numeric` operands come in operator/value pairs; a dangling operator has no operand.
        await ExpectPatternInvalid(ev, """{"detail": {"amount": [{"numeric": [">"]}]}}""",
            "a numeric comparison with an operator but no operand",
            "Value of > must be numeric", 41);
        await ExpectPatternValid(ev, """{"detail": {"amount": [{"numeric": [">", 100]}]}}""",
            Event(ctx, detail: """{"amount": 101}"""), true,
            "the completed numeric comparison is accepted and matches");

        // `exists` takes a JSON boolean, not the string spelling of one.
        await ExpectPatternInvalid(ev, """{"detail": {"customer": [{"exists": "true"}]}}""",
            "an exists operand that is a string rather than a boolean",
            "exists match pattern must be either true or false.", 38);
        await ExpectPatternValid(ev, """{"detail": {"customer": [{"exists": true}]}}""",
            Event(ctx, detail: """{"customer": "C1"}"""), true,
            "the boolean exists operand is accepted and matches");

        // The root of a pattern is a JSON object. An array root has no field to bind to.
        await ExpectPatternInvalid(ev, """["sim2real.orders"]""", "a JSON array at the root of the pattern",
            "Filter is not an object", 2);
        // ...and neither does a bare scalar root.
        await ExpectPatternInvalid(ev, "\"sim2real.orders\"", "a bare JSON string at the root of the pattern",
            "Filter is not an object", 2);
        await ExpectPatternValid(ev, """{"source": ["sim2real.orders"]}""",
            Event(ctx), true, "an object root is accepted and matches");

        var name = FidelityContext.UniqueName("sim2real-behav-");
        const string good = """{"source": ["sim2real.orders"]}""";
        await ev.PutRuleAsync(new PutRuleRequest
        {
            Name = name,
            EventPattern = good,
            State = RuleState.ENABLED,
            Description = "sim2real behavioral: malformed-pattern rejection",
            Tags = [new Tag { Key = "sim2real:managed", Value = "true" }],
        });
        ctx.OnCleanup(() => ev.DeleteRuleAsync(new DeleteRuleRequest { Name = name }));

        var stored = (await ev.DescribeRuleAsync(new DescribeRuleRequest { Name = name })).EventPattern;
        Ensure(
            JsonNode.DeepEquals(JsonNode.Parse(stored), JsonNode.Parse(good)),
            $"setup failed: rule {name} stored EventPattern '{stored}', expected '{good}'");

        (string Body, string Why, string Reason, int Column)[] cases =
        [
            ("""{"source": "sim2real.orders"}""", "a leaf value not wrapped in an array",
                "\"source\" must be an object or an array", 13),
            ("""{"source": [{"starts-with": "sim2real."}]}""", "an operator key that does not exist",
                "Unrecognized match type starts-with", 30),
            ("""{"detail": {"amount": [{"numeric": [">"]}]}}""", "a dangling numeric operator",
                "Value of > must be numeric", 41),
            ("""{"detail": {"customer": [{"exists": "true"}]}}""", "a non-boolean exists operand",
                "exists match pattern must be either true or false.", 38),
            ("""["sim2real.orders"]""", "a JSON array at the pattern root", "Filter is not an object", 2),
        ];
        foreach (var (body, why, reason, column) in cases)
        {
            var (code, message) = await Error(() => ev.PutRuleAsync(new PutRuleRequest { Name = name, EventPattern = body, State = RuleState.ENABLED }));
            Ensure(
                code == "InvalidEventPatternException",
                $"PutRule must reject {why}: expected InvalidEventPatternException, got {code}\n" +
                $"  pattern = {body}");
            var expected = PatternMessage(body, reason, column);
            Ensure(
                message == expected,
                $"PutRule refused {why} with message '{message}', expected '{expected}'");
            var after = await ev.DescribeRuleAsync(new DescribeRuleRequest { Name = name });
            Ensure(
                JsonNode.DeepEquals(JsonNode.Parse(after.EventPattern), JsonNode.Parse(good)),
                $"PutRule rejected {why} but the stored EventPattern changed to " +
                $"'{after.EventPattern}'; a refused upsert must leave the rule byte-identical");
            Ensure(
                after.State?.Value == "ENABLED",
                $"PutRule rejected {why} but the rule State changed to '{after.State?.Value}'");
        }

        // A valid upsert still works, so the refusals were specific to the pattern.
        const string better = """{"source": ["sim2real.orders"], "detail-type": ["OrderPlaced"]}""";
        await ev.PutRuleAsync(new PutRuleRequest { Name = name, EventPattern = better, State = RuleState.ENABLED });
        var final = (await ev.DescribeRuleAsync(new DescribeRuleRequest { Name = name })).EventPattern;
        Ensure(
            JsonNode.DeepEquals(JsonNode.Parse(final), JsonNode.Parse(better)),
            $"control failed: after a valid upsert the rule holds '{final}', expected '{better}'");
    }

    /// <summary>A complete, well-formed EventBridge envelope, with any field overridable.</summary>
    /// <remarks><paramref name="detail"/> is JSON text.</remarks>
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

    /// <summary>Run <paramref name="call"/> and return the (code, message) of its AWS error; a success raises.</summary>
    private static async Task<(string Code, string Message)> Error<T>(Func<Task<T>> call)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(call);
        if (error is null)
        {
            throw Fail($"expected the malformed pattern to be refused; it SUCCEEDED: {Describe(result)}");
        }

        return (error.Code ?? "<missing Code>", error.Message ?? "<missing Message>");
    }

    private static string Describe(object? result) => result switch
    {
        TestEventPatternResponse r => $"{{'Result': {r.Result}}}",
        PutRuleResponse r => $"{{'RuleArn': '{r.RuleArn}'}}",
        _ => $"{result}",
    };

    /// <summary>The exact InvalidEventPatternException message: the reason and parser position in <paramref name="body"/>.</summary>
    private static string PatternMessage(string body, string reason, int column) =>
        $"Event pattern is not valid. Reason: {reason}\n" +
        $" at [Source: (String)\"{body}\"; line: 1, column: {column}]";

    /// <summary>TestEventPattern must refuse to evaluate a structurally malformed pattern, saying why.</summary>
    /// <remarks><paramref name="body"/> is <c>json.dumps(pattern)</c>.</remarks>
    private static async Task ExpectPatternInvalid(AmazonEventBridgeClient ev, string body, string why, string reason, int column)
    {
        var (code, message) = await Error(() => ev.TestEventPatternAsync(new TestEventPatternRequest
        {
            EventPattern = body, Event = """{"source": "x"}""",
        }));
        Ensure(
            code == "InvalidEventPatternException",
            $"TestEventPattern must reject {why}: expected InvalidEventPatternException, got {code}\n" +
            $"  pattern = {body}");
        var expected = PatternMessage(body, reason, column);
        Ensure(
            message == expected,
            $"TestEventPattern refused {why} with message '{message}', expected '{expected}'");
    }

    /// <summary>TestEventPattern must accept the pattern and return the expected verdict.</summary>
    private static async Task ExpectPatternValid(AmazonEventBridgeClient ev, string pattern, JsonObject @event, bool want, string why)
    {
        var resp = await ev.TestEventPatternAsync(new TestEventPatternRequest { EventPattern = pattern, Event = PyDumps(@event) });
        var result = resp.Result;
        Ensure(result is not null, "TestEventPattern.Result must be a JSON boolean, got None (NoneType)");
        Ensure(
            result == want,
            $"{why}: expected Result={want}, got {result}\n" +
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
