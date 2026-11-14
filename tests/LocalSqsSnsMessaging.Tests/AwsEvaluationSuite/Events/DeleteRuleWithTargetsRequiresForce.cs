// Translated from behavioral/events/check_delete_rule_with_targets_requires_force.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.EventBridge;
using Amazon.EventBridge.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Events;

/// <summary>events.delete-rule-with-targets-requires-force</summary>
[Category("AwsEvaluationSuite")]
public sealed class DeleteRuleWithTargetsRequiresForce
{
    /// <summary>
    /// DeleteRule must refuse to drop a rule that still has targets, but must succeed on the
    /// identical call once those targets are removed — the restore leg is what tells "correctly
    /// rejects a rule with targets" apart from "rejects every DeleteRule call".
    /// </summary>
    [Test]
    [DisplayName("events.delete-rule-with-targets-requires-force")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ev = ctx.Events;
        var bus = FidelityContext.UniqueName("sim2real-behav-bus-");
        var busResp = await ev.CreateEventBusAsync(new CreateEventBusRequest { Name = bus });
        ctx.OnCleanup(() => ev.DeleteEventBusAsync(new DeleteEventBusRequest { Name = bus }));
        await ev.TagResourceAsync(new TagResourceRequest { ResourceARN = busResp.EventBusArn, Tags = [new Tag { Key = "sim2real", Value = "behav" }] });

        // ---- positive control: DeleteRule on a target-free rule succeeds on this same bus ----
        var controlRule = FidelityContext.UniqueName("sim2real-behav-rule-control-");
        var controlResp = await ev.PutRuleAsync(new PutRuleRequest { Name = controlRule, EventBusName = bus, EventPattern = """{"source": ["sim2real.behav"]}""" });
        ctx.OnCleanup(() => CleanupRule(ev, controlRule, bus));
        await ev.TagResourceAsync(new TagResourceRequest { ResourceARN = controlResp.RuleArn, Tags = [new Tag { Key = "sim2real", Value = "behav" }] });
        await ev.DeleteRuleAsync(new DeleteRuleRequest { Name = controlRule, EventBusName = bus });
        var controlErr = await Error(() => ev.DescribeRuleAsync(new DescribeRuleRequest { Name = controlRule, EventBusName = bus }));
        Ensure(
            controlErr == ("ResourceNotFoundException", RuleNotFound(controlRule, bus)),
            "positive control: DeleteRule on a target-free rule must succeed, but describe_rule " +
            $"afterwards returned {Repr(controlErr)}");

        // ---- break exactly one thing: attach a target to a second rule ----
        var rule = FidelityContext.UniqueName("sim2real-behav-rule-");
        var ruleResp = await ev.PutRuleAsync(new PutRuleRequest { Name = rule, EventBusName = bus, EventPattern = """{"source": ["sim2real.behav"]}""" });
        ctx.OnCleanup(() => CleanupRule(ev, rule, bus));
        await ev.TagResourceAsync(new TagResourceRequest { ResourceARN = ruleResp.RuleArn, Tags = [new Tag { Key = "sim2real", Value = "behav" }] });
        await ev.PutTargetsAsync(new PutTargetsRequest
        {
            Rule = rule,
            EventBusName = bus,
            Targets = [new Target { Id = "t1", Arn = LambdaTargetArn(ruleResp.RuleArn) }],
        });
        var err = await Error(() => ev.DeleteRuleAsync(new DeleteRuleRequest { Name = rule, EventBusName = bus }));
        Ensure(
            err == ("ValidationException", "Rule can't be deleted since it has targets."),
            $"expected DeleteRule with attached targets to fail with ValidationException, got {Repr(err)}");
        var desc = await ev.DescribeRuleAsync(new DescribeRuleRequest { Name = rule, EventBusName = bus });
        Ensure(desc.Name == rule, "rule must survive a rejected DeleteRule");

        // ---- restore: remove the target, then the identical DeleteRule call now succeeds ----
        await ev.RemoveTargetsAsync(new RemoveTargetsRequest { Rule = rule, EventBusName = bus, Ids = ["t1"] });
        var restoredCode = (await FidelityContext.CaptureError(() => ev.DeleteRuleAsync(new DeleteRuleRequest { Name = rule, EventBusName = bus })))?.Code;
        Ensure(
            restoredCode is null,
            $"expected DeleteRule to succeed once the rule has no targets, got {restoredCode}");
        var afterErr = await Error(() => ev.DescribeRuleAsync(new DescribeRuleRequest { Name = rule, EventBusName = bus }));
        Ensure(
            afterErr == ("ResourceNotFoundException", RuleNotFound(rule, bus)),
            $"expected the rule to be gone after the restored DeleteRule, describe_rule returned {Repr(afterErr)}");
    }

    /// <summary>Syntactic Lambda target ARN in ref_arn's partition/region/account (EventBridge does not verify existence).</summary>
    private static string LambdaTargetArn(string refArn)
    {
        var p = refArn.Split(':');
        return $"arn:{p[1]}:lambda:{p[3]}:{p[4]}:function:sim2real-behav-fn-{Guid.NewGuid().ToString("N")[..8]}";
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

    /// <summary>(code, message) of a call that must fail; a call that succeeds raises AssertionError.</summary>
    private static async Task<(string? Code, string? Message)> Error<T>(Func<Task<T>> fn)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is null)
        {
            throw Fail($"expected a ClientError, but the call succeeded and returned {result}");
        }

        return (error.Code, error.Message);
    }

    private static string RuleNotFound(string name, string bus) => $"Rule {name} does not exist on EventBus {bus}.";

    private static string Repr((string? Code, string? Message) e) => $"('{e.Code}', '{e.Message}')";
}
