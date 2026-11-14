// Translated from behavioral/events/check_rule_scoped_to_event_bus_isolation.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.EventBridge;
using Amazon.EventBridge.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Events;

/// <summary>events.rule-scoped-to-event-bus-isolation</summary>
[Category("AwsEvaluationSuite")]
public sealed class RuleScopedToEventBusIsolation
{
    private const string Check = "rule-scoped-to-event-bus-isolation";

    /// <summary>
    /// A rule created on a custom event bus is not visible via ListRules on the default bus, but is visible when listing its own bus -- rules are bus-scoped.
    /// </summary>
    [Test]
    [DisplayName("events.rule-scoped-to-event-bus-isolation")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ev = ctx.Events;
        var busName = FidelityContext.UniqueName("sim2real-behav-bus-");
        await ev.CreateEventBusAsync(new CreateEventBusRequest { Name = busName, Tags = Tags() });
        ctx.OnCleanup(() => ev.DeleteEventBusAsync(new DeleteEventBusRequest { Name = busName }));

        var ruleName = FidelityContext.UniqueName("sim2real-behav-rule-");
        const string pattern = """{"source": ["x"]}""";
        await ev.PutRuleAsync(new PutRuleRequest
        {
            Name = ruleName,
            EventPattern = pattern,
            EventBusName = busName,
            Tags = Tags(),
        });
        ctx.OnCleanup(() => CleanupRule(ev, ruleName, busName));

        // Positive control: DescribeRule itself works and returns the bus-qualified rule
        // when the caller names the owning bus -- without this, a 404 in the negative
        // below would be indistinguishable from DescribeRule being broken outright.
        var described = await ev.DescribeRuleAsync(new DescribeRuleRequest { Name = ruleName, EventBusName = busName });
        Ensure(
            described.EventBusName == busName,
            $"describing bus-scoped rule with its own EventBusName must succeed and echo it, got EventBusName='{described.EventBusName}'");
        Ensure(
            described.EventPattern == pattern,
            $"describing bus-scoped rule with its own EventBusName must return the rule's own pattern, expected '{pattern}' got '{described.EventPattern}'");

        var defaultRules = (await RuleNames(ev, ruleName, null)).ToHashSet();
        Ensure(!defaultRules.Contains(ruleName), "rule created on custom bus must not appear when listing the default bus");

        var customRules = (await RuleNames(ev, ruleName, busName)).ToHashSet();
        Ensure(customRules.Contains(ruleName), "rule must be visible when listing its own bus");

        var err = await ClientError(() => ev.DescribeRuleAsync(new DescribeRuleRequest { Name = ruleName }));
        Ensure(err.Code == "ResourceNotFoundException", $"describing bus-scoped rule without EventBusName must 404, got {err}");
        var expectedMessage = $"Rule {ruleName} does not exist on EventBus default.";
        Ensure(err.Message == expectedMessage, $"expected message '{expectedMessage}', got '{err.Message}'");
    }

    private static List<Tag> Tags() => [new Tag { Key = "sim2real-check", Value = Check }];

    // Python indexes list_rules(...)["Rules"], which raises KeyError when the response omits it.
    private static async Task<IEnumerable<string>> RuleNames(AmazonEventBridgeClient ev, string prefix, string? bus) =>
        ((await ev.ListRulesAsync(new ListRulesRequest { NamePrefix = prefix, EventBusName = bus })).Rules
            ?? throw new KeyNotFoundException("'Rules'"))
        .Select(r => r.Name);

    /// <summary>
    /// Call fn(); return the botocore Error dict it raised. An unexpected SUCCESS raises, so a
    /// target that accepts what AWS refuses is never read as a code mismatch.
    /// </summary>
    private static async Task<AwsError> ClientError<T>(Func<Task<T>> fn)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        return error ?? throw Fail($"expected this call to be rejected, but it SUCCEEDED and returned {result}");
    }

    /// <summary>Remove any attached targets, then delete the rule (delete_rule refuses a rule with targets).</summary>
    private static async Task CleanupRule(AmazonEventBridgeClient client, string name, string bus)
    {
        var error = await FidelityContext.CaptureError(async () =>
        {
            var ids = ((await client.ListTargetsByRuleAsync(new ListTargetsByRuleRequest { Rule = name, EventBusName = bus })).Targets ?? [])
                .Select(t => t.Id).ToList();
            if (ids.Count > 0)
            {
                await client.RemoveTargetsAsync(new RemoveTargetsRequest { Rule = name, Ids = ids, EventBusName = bus });
            }
        });
        if (error is not null && error.Code != "ResourceNotFoundException")
        {
            throw error.Exception;
        }

        await client.DeleteRuleAsync(new DeleteRuleRequest { Name = name, EventBusName = bus, Force = true });
    }
}
