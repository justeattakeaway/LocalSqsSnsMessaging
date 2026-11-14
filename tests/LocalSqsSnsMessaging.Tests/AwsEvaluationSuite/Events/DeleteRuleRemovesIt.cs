// Translated from behavioral/events/check_delete_rule_removes_it.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.EventBridge;
using Amazon.EventBridge.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Events;

/// <summary>events.delete-rule-removes-it</summary>
[Category("AwsEvaluationSuite")]
public sealed class DeleteRuleRemovesIt
{
    /// <summary>
    /// DeleteRule removes the rule: an independent describe reports NotFound, a
    /// mutation on the dead id is likewise refused, and the freed name is reusable
    /// for a brand-new rule.
    /// </summary>
    [Test]
    [DisplayName("events.delete-rule-removes-it")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ev = ctx.Events;
        var bus = FidelityContext.UniqueName("sim2real-behav-bus-");
        await ev.CreateEventBusAsync(new CreateEventBusRequest { Name = bus });
        ctx.OnCleanup(() => ev.DeleteEventBusAsync(new DeleteEventBusRequest { Name = bus }));
        var rule = FidelityContext.UniqueName("sim2real-behav-rule-");
        await ev.PutRuleAsync(new PutRuleRequest { Name = rule, EventBusName = bus, EventPattern = """{"source": ["sim2real.behav"]}""" });
        ctx.OnCleanup(() => CleanupRule(ev, rule, bus));
        await ev.DeleteRuleAsync(new DeleteRuleRequest { Name = rule, EventBusName = bus });
        var expected = ("ResourceNotFoundException", $"Rule {rule} does not exist on EventBus {bus}.");
        var err = await Error(() => ev.DescribeRuleAsync(new DescribeRuleRequest { Name = rule, EventBusName = bus }));
        Ensure(err == expected, $"expected {Repr(expected)} from DescribeRule on a deleted rule, got {Repr(err)}");

        var mutateErr = await Error(() => ev.EnableRuleAsync(new EnableRuleRequest { Name = rule, EventBusName = bus }));
        Ensure(
            mutateErr == expected,
            $"expected {Repr(expected)} from EnableRule on a deleted rule, got {Repr(mutateErr)}");

        await ev.PutRuleAsync(new PutRuleRequest { Name = rule, EventBusName = bus, EventPattern = """{"source": ["sim2real.behav.reused"]}""" });
        var reused = await ev.DescribeRuleAsync(new DescribeRuleRequest { Name = rule, EventBusName = bus });
        Ensure(reused.State?.Value == "ENABLED", $"expected the freed rule name to be reusable, got State='{reused.State?.Value}'");
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

    /// <summary>Call fn(); return its (code, message). An unexpected SUCCESS raises.</summary>
    private static async Task<(string? Code, string? Message)> Error<T>(Func<Task<T>> fn)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is null)
        {
            throw Fail($"expected a ClientError, but the call SUCCEEDED and returned {result}");
        }

        return (error.Code, error.Message);
    }

    private static string Repr((string? Code, string? Message) e) => $"('{e.Code}', '{e.Message}')";
}
