// Translated from behavioral/events/check_enable_disable_rule_roundtrip.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.EventBridge;
using Amazon.EventBridge.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Events;

/// <summary>events.enable-disable-rule-roundtrip</summary>
[Category("AwsEvaluationSuite")]
public sealed class EnableDisableRuleRoundtrip
{
    private const string Check = "enable-disable-rule-roundtrip";

    /// <summary>DisableRule/EnableRule transition RuleState between ENABLED and DISABLED.</summary>
    [Test]
    [DisplayName("events.enable-disable-rule-roundtrip")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ev = ctx.Events;
        var bus = FidelityContext.UniqueName("sim2real-behav-bus-");
        await ev.CreateEventBusAsync(new CreateEventBusRequest { Name = bus, Tags = [new Tag { Key = "sim2real-check", Value = Check }] });
        ctx.OnCleanup(() => ev.DeleteEventBusAsync(new DeleteEventBusRequest { Name = bus }));
        var rule = FidelityContext.UniqueName("sim2real-behav-rule-");
        await ev.PutRuleAsync(new PutRuleRequest
        {
            Name = rule,
            EventBusName = bus,
            EventPattern = """{"source": ["sim2real.behav"]}""",
            Tags = [new Tag { Key = "sim2real-check", Value = Check }],
        });
        ctx.OnCleanup(() => CleanupRule(ev, rule, bus));
        await ev.DisableRuleAsync(new DisableRuleRequest { Name = rule, EventBusName = bus });
        var desc = await ev.DescribeRuleAsync(new DescribeRuleRequest { Name = rule, EventBusName = bus });
        Ensure(desc.State?.Value == "DISABLED", $"expected DISABLED after DisableRule, got {desc.State?.Value}");
        await ev.EnableRuleAsync(new EnableRuleRequest { Name = rule, EventBusName = bus });
        desc = await ev.DescribeRuleAsync(new DescribeRuleRequest { Name = rule, EventBusName = bus });
        Ensure(desc.State?.Value == "ENABLED", $"expected ENABLED after EnableRule, got {desc.State?.Value}");
    }

    /// <summary>Remove any attached targets, then delete the rule (delete_rule refuses a rule with targets).</summary>
    private static async Task CleanupRule(IAmazonEventBridge client, string name, string bus)
    {
        try
        {
            var ids = ((await client.ListTargetsByRuleAsync(new ListTargetsByRuleRequest { Rule = name, EventBusName = bus })).Targets ?? [])
                .Select(t => t.Id).ToList();
            if (ids.Count > 0)
            {
                await client.RemoveTargetsAsync(new RemoveTargetsRequest { Rule = name, Ids = ids, EventBusName = bus });
            }
        }
        catch (Amazon.EventBridge.Model.ResourceNotFoundException)
        {
        }

        await client.DeleteRuleAsync(new DeleteRuleRequest { Name = name, EventBusName = bus, Force = true });
    }
}
