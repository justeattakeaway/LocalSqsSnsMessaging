// Translated from behavioral/events/check_put_rule_invalid_pattern_fails.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.EventBridge.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Events;

/// <summary>events.put-rule-invalid-pattern-fails</summary>
[Category("AwsEvaluationSuite")]
public sealed class PutRuleInvalidPatternFails
{
    private const string Check = "put-rule-invalid-pattern-fails";

    /// <summary>
    /// PutRule rejects malformed EventPattern JSON with InvalidEventPatternException, leaves no rule
    /// behind, and a valid pattern under the same name is then accepted and round-trips.
    /// </summary>
    [Test]
    [DisplayName("events.put-rule-invalid-pattern-fails")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ev = ctx.Events;
        var bus = FidelityContext.UniqueName("sim2real-behav-bus-");
        await ev.CreateEventBusAsync(new CreateEventBusRequest { Name = bus, Tags = [new Tag { Key = "sim2real-check", Value = Check }] });
        ctx.OnCleanup(() => ev.DeleteEventBusAsync(new DeleteEventBusRequest { Name = bus }));
        var rule = FidelityContext.UniqueName("sim2real-behav-rule-");

        const string badPattern = "{not valid json";
        var err = await ClientError(() => ev.PutRuleAsync(new PutRuleRequest { Name = rule, EventBusName = bus, EventPattern = badPattern }));
        Ensure(
            err.Code == "InvalidEventPatternException",
            $"PutRule must reject malformed EventPattern JSON: expected InvalidEventPatternException, " +
            $"got {err}");
        var expectedMessage =
            "Event pattern is not valid. Reason: Unexpected character ('n' (code 110)): was expecting " +
            $"double-quote to start field name\n at [Source: (String)\"{badPattern}\"; line: 1, column: 2]";
        Ensure(err.Message == expectedMessage, $"expected message '{expectedMessage}', got '{err.Message}'");

        // Effect absent: a refused PutRule must not have created a rule under that name.
        var listed = ((await ev.ListRulesAsync(new ListRulesRequest { EventBusName = bus, NamePrefix = rule })).Rules ?? []).Select(r => r.Name).ToList();
        Ensure(
            listed.Count == 0,
            $"PutRule rejected malformed EventPattern but ListRules(NamePrefix='{rule}') returned " +
            $"[{string.Join(", ", listed.Select(n => $"'{n}'"))}]; a refused create must leave no rule behind");
        var describeErr = await ClientError(() => ev.DescribeRuleAsync(new DescribeRuleRequest { Name = rule, EventBusName = bus }));
        Ensure(
            describeErr.Code == "ResourceNotFoundException",
            $"PutRule rejected malformed EventPattern but DescribeRule('{rule}') returned " +
            $"{describeErr} instead of ResourceNotFoundException");
        var expectedDescribeMessage = $"Rule {rule} does not exist on EventBus {bus}.";
        Ensure(
            describeErr.Message == expectedDescribeMessage,
            $"expected message '{expectedDescribeMessage}', got '{describeErr.Message}'");

        // Positive control, same bus and same rule name: a well-formed pattern is accepted and
        // round-trips byte-identically through DescribeRule.
        const string goodPattern = """{"source": ["aws.ec2"]}""";
        await ev.PutRuleAsync(new PutRuleRequest { Name = rule, EventBusName = bus, EventPattern = goodPattern });
        ctx.OnCleanup(() => ev.DeleteRuleAsync(new DeleteRuleRequest { Name = rule, EventBusName = bus }));
        var described = await ev.DescribeRuleAsync(new DescribeRuleRequest { Name = rule, EventBusName = bus });
        Ensure(
            described.EventPattern == goodPattern,
            $"positive control: PutRule with a well-formed pattern under the same name that was just " +
            $"refused must round-trip through DescribeRule; got '{described.EventPattern}', " +
            $"submitted '{goodPattern}'");
    }

    /// <summary>Botocore Error dict of a call that must fail; an unexpected success raises.</summary>
    private static async Task<AwsError> ClientError<T>(Func<Task<T>> call)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(call);
        return error ?? throw Fail($"expected this call to be rejected, but it SUCCEEDED and returned {result}");
    }
}
