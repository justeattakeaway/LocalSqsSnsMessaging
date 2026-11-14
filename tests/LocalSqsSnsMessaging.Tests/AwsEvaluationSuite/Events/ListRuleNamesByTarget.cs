// Translated from behavioral/events/check_list_rule_names_by_target.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.EventBridge.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Events;

/// <summary>events.list-rule-names-by-target</summary>
[Category("AwsEvaluationSuite")]
public sealed class ListRuleNamesByTarget
{
    private const string Check = "list-rule-names-by-target";

    /// <summary>
    /// ListRuleNamesByTarget resolves the reverse rule&lt;-target mapping for a target ARN attached via
    /// PutTargets, and stops reporting it after RemoveTargets.
    /// </summary>
    [Test]
    [DisplayName("events.list-rule-names-by-target")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ev = ctx.Events;
        List<Tag> tag = [new Tag { Key = "sim2real-behav", Value = Check }];
        var bus = FidelityContext.UniqueName("sim2real-behav-bus-");
        await ev.CreateEventBusAsync(new CreateEventBusRequest { Name = bus, Tags = tag });
        ctx.OnCleanup(() => ev.DeleteEventBusAsync(new DeleteEventBusRequest { Name = bus }));
        var rule = FidelityContext.UniqueName("sim2real-behav-rule-");
        var ruleResp = await ev.PutRuleAsync(new PutRuleRequest
        {
            Name = rule,
            EventBusName = bus,
            EventPattern = """{"source": ["sim2real.behav"]}""",
            Tags = tag,
        });
        ctx.OnCleanup(() => CleanupRule(ev, rule, bus));

        var targetArn = LambdaTargetArn(ruleResp.RuleArn);
        await ev.PutTargetsAsync(new PutTargetsRequest { Rule = rule, EventBusName = bus, Targets = [new Target { Id = "t1", Arn = targetArn }] });

        // Python indexes ["RuleNames"], which raises KeyError when the response omits it.
        var names = (await ev.ListRuleNamesByTargetAsync(new ListRuleNamesByTargetRequest { TargetArn = targetArn, EventBusName = bus })).RuleNames
            ?? throw new KeyNotFoundException("'RuleNames'");
        Ensure(names.Contains(rule), $"expected {rule} in reverse lookup, got {Repr(names)}");

        await ev.RemoveTargetsAsync(new RemoveTargetsRequest { Rule = rule, EventBusName = bus, Ids = ["t1"] });
        var names2 = (await ev.ListRuleNamesByTargetAsync(new ListRuleNamesByTargetRequest { TargetArn = targetArn, EventBusName = bus })).RuleNames
            ?? throw new KeyNotFoundException("'RuleNames'");
        Ensure(!names2.Contains(rule), $"reverse lookup must drop detached rule, got {Repr(names2)}");
    }

    /// <summary>Syntactic Lambda target ARN in ref_arn's partition/region/account (EventBridge does not verify existence).</summary>
    private static string LambdaTargetArn(string refArn)
    {
        var p = refArn.Split(':');
        return $"arn:{p[1]}:lambda:{p[3]}:{p[4]}:function:sim2real-behav-fn-{Guid.NewGuid().ToString("N")[..8]}";
    }

    /// <summary>Remove any attached targets, then delete the rule (delete_rule refuses a rule with targets).</summary>
    private static async Task CleanupRule(Amazon.EventBridge.AmazonEventBridgeClient client, string name, string bus)
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

    private static string Repr(IEnumerable<string> items) => $"[{string.Join(", ", items.Select(i => $"'{i}'"))}]";
}
