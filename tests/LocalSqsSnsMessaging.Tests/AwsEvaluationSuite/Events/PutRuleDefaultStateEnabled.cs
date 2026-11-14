// Translated from behavioral/events/check_put_rule_default_state_enabled.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.EventBridge;
using Amazon.EventBridge.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Events;

/// <summary>events.put-rule-default-state-enabled</summary>
[Category("AwsEvaluationSuite")]
public sealed class PutRuleDefaultStateEnabled
{
    /// <summary>
    /// PutRule without an explicit State defaults the rule to ENABLED, while a sibling rule
    /// created with State='DISABLED' stays DISABLED, so ENABLED is not merely hardcoded.
    /// </summary>
    [Test]
    [DisplayName("events.put-rule-default-state-enabled")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ev = ctx.Events;
        var bus = FidelityContext.UniqueName("sim2real-behav-bus-");
        await ev.CreateEventBusAsync(new CreateEventBusRequest { Name = bus });
        ctx.OnCleanup(() => ev.DeleteEventBusAsync(new DeleteEventBusRequest { Name = bus }));

        var rule = FidelityContext.UniqueName("sim2real-behav-rule-");
        await ev.PutRuleAsync(new PutRuleRequest { Name = rule, EventBusName = bus, EventPattern = """{"source": ["test.source"]}""" });
        ctx.OnCleanup(() => CleanupRule(ev, rule, bus));
        var desc = await ev.DescribeRuleAsync(new DescribeRuleRequest { Name = rule, EventBusName = bus });
        Ensure(desc.State?.Value == "ENABLED", $"rule created without State must default to ENABLED, got '{desc.State?.Value}'");

        var disabledRule = FidelityContext.UniqueName("sim2real-behav-rule-disabled-");
        await ev.PutRuleAsync(new PutRuleRequest
        {
            Name = disabledRule,
            EventBusName = bus,
            EventPattern = """{"source": ["test.source"]}""",
            State = RuleState.DISABLED,
        });
        ctx.OnCleanup(() => CleanupRule(ev, disabledRule, bus));
        var disabledDesc = await ev.DescribeRuleAsync(new DescribeRuleRequest { Name = disabledRule, EventBusName = bus });
        Ensure(
            disabledDesc.State?.Value == "DISABLED",
            $"rule created with State='DISABLED' must stay DISABLED, got '{disabledDesc.State?.Value}'");
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
