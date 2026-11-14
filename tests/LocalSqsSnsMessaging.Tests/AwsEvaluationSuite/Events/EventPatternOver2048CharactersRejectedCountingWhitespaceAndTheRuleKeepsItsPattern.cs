// Translated from behavioral/events/check_event_pattern_over_2048_characters_rejected_counting_whitespace_and_the_rule_keeps_its_pattern.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.EventBridge;
using Amazon.EventBridge.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Events;

/// <summary>events.event-pattern-over-2048-characters-rejected-counting-whitespace-and-the-rule-keeps-its-pattern</summary>
[Category("AwsEvaluationSuite")]
public sealed class EventPatternOver2048CharactersRejectedCountingWhitespaceAndTheRuleKeepsItsPattern
{
    private const string Check = "event-pattern-over-2048-characters-rejected-counting-whitespace-and-the-rule-keeps-its-pattern";
    private const int Limit = 2048;

    /// <summary>
    /// PutRule accepts a 2048-character pattern and refuses 2049, counting the text as sent
    /// (a pretty-printed pattern whose compact form fits is refused); refusals keep the stored pattern.
    /// </summary>
    [Test]
    [DisplayName("events.event-pattern-over-2048-characters-rejected-counting-whitespace-and-the-rule-keeps-its-pattern")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var events = ctx.Events;
        var bus = FidelityContext.UniqueName("sim2real-behav-bus-");
        await events.CreateEventBusAsync(new CreateEventBusRequest { Name = bus, Tags = [new Tag { Key = "sim2real", Value = Check }] });
        ctx.OnCleanup(() => events.DeleteEventBusAsync(new DeleteEventBusRequest { Name = bus }));
        var rule = FidelityContext.UniqueName("sim2real-behav-rule-");
        await events.PutRuleAsync(new PutRuleRequest { Name = rule, EventBusName = bus, EventPattern = """{"source": ["sim2real.limit"]}""" });
        ctx.OnCleanup(() => events.DeleteRuleAsync(new DeleteRuleRequest { Name = rule, EventBusName = bus }));

        static (string Text, string Value) Sized(int n)
        {
            var value = new string('y', n - """{"source":[""]}""".Length);
            var text = $$"""{"source":["{{value}}"]}""";
            Ensure(text.Length == n, "assert len(text) == n");
            return (text, value);
        }

        var (atLimit, value) = Sized(Limit);
        await events.PutRuleAsync(new PutRuleRequest { Name = rule, EventBusName = bus, EventPattern = atLimit });
        var stored = (await events.DescribeRuleAsync(new DescribeRuleRequest { Name = rule, EventBusName = bus })).EventPattern;
        Ensure(stored == atLimit, $"a {Limit}-character pattern was stored as {stored.Length} characters");

        var refusal = ("InvalidEventPatternException",
                       $"Parameter EventPattern for rule {rule} exceeds limit of {Limit}.");
        var (over, _) = Sized(Limit + 1);
        var got = await Error(() => events.PutRuleAsync(new PutRuleRequest { Name = rule, EventBusName = bus, EventPattern = over }));
        Ensure(got == refusal, $"a {Limit + 1}-character pattern gave {Repr(got)}");
        await AssertKept(ctx, events, rule, bus, atLimit, value);

        // The whitespace case separates measuring the request text from a re-serialised pattern.
        var sources = Enumerable.Range(0, 120).Select(i => $"sim2real.s{i:000}").ToList();
        // json.dumps(..., separators=(",", ":")) and json.dumps(..., indent=2).
        var compact = "{\"source\":[" + string.Join(",", sources.Select(s => $"\"{s}\"")) + "]}";
        var pretty = "{\n  \"source\": [\n" + string.Join(",\n", sources.Select(s => $"    \"{s}\"")) + "\n  ]\n}";
        Ensure(compact.Length < Limit && Limit < pretty.Length, $"({compact.Length}, {pretty.Length})");
        got = await Error(() => events.PutRuleAsync(new PutRuleRequest { Name = rule, EventBusName = bus, EventPattern = pretty }));
        Ensure(
            got == refusal,
            $"a pattern of {pretty.Length} characters as sent ({compact.Length} compact) gave {Repr(got)}");
        await AssertKept(ctx, events, rule, bus, atLimit, value);

        await events.PutRuleAsync(new PutRuleRequest { Name = rule, EventBusName = bus, EventPattern = compact });
        Ensure(
            (await events.DescribeRuleAsync(new DescribeRuleRequest { Name = rule, EventBusName = bus })).EventPattern == compact,
            "the compact form of the refused pattern was not accepted and stored verbatim");
    }

    private static async Task AssertKept(FidelityContext ctx, IAmazonEventBridge events, string rule, string bus, string pattern, string value)
    {
        var stored = (await events.DescribeRuleAsync(new DescribeRuleRequest { Name = rule, EventBusName = bus })).EventPattern;
        Ensure(
            stored == pattern,
            $"a refused update changed the stored pattern ({stored.Length} characters, expected {pattern.Length})");
        var @event = $$$"""{"id": "1", "account": "{{{ctx.Account}}}", "source": "{{{value}}}", "time": "2026-01-01T00:00:00Z", "region": "{{{ctx.Region}}}", "resources": [], "detail-type": "t", "detail": {}}""";
        Ensure(
            (await events.TestEventPatternAsync(new TestEventPatternRequest { EventPattern = stored, Event = @event })).Result == true,
            "the kept pattern no longer matches the event it was written for");
    }

    private static async Task<(string? Code, string? Message)> Error(Func<Task> fn)
    {
        var error = await FidelityContext.CaptureError(fn);
        if (error is null)
        {
            throw Fail("the call succeeded; real AWS refuses it");
        }

        return (error.Code, error.Message ?? "");
    }

    private static string Repr((string? Code, string? Message) e) => $"('{e.Code}', '{e.Message}')";
}
