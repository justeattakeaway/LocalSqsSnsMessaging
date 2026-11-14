// Translated from behavioral/events/check_put_targets_and_list.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.EventBridge;
using Amazon.EventBridge.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Events;

/// <summary>events.put-targets-and-list</summary>
[Category("AwsEvaluationSuite")]
public sealed class PutTargetsAndList
{
    /// <summary>PutTargets attaches a target that is then visible, with its Arn intact, via ListTargetsByRule.</summary>
    [Test]
    [DisplayName("events.put-targets-and-list")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ev = ctx.Events;
        var bus = FidelityContext.UniqueName("sim2real-behav-bus-");
        await ev.CreateEventBusAsync(new CreateEventBusRequest { Name = bus, Tags = [new Tag { Key = "sim2real", Value = "behavioral" }] });
        ctx.OnCleanup(() => ev.DeleteEventBusAsync(new DeleteEventBusRequest { Name = bus }));
        var rule = FidelityContext.UniqueName("sim2real-behav-rule-");
        var ruleResp = await ev.PutRuleAsync(new PutRuleRequest
        {
            Name = rule,
            EventBusName = bus,
            EventPattern = """{"source": ["sim2real.behav"]}""",
            Tags = [new Tag { Key = "sim2real", Value = "behavioral" }],
        });
        ctx.OnCleanup(() => CleanupRule(ev, rule, bus));
        var targetArn = LambdaTargetArn(ruleResp.RuleArn);
        var resp = await ev.PutTargetsAsync(new PutTargetsRequest
        {
            Rule = rule,
            EventBusName = bus,
            Targets = [new Target { Id = "t1", Arn = targetArn }],
        });
        Ensure((resp.FailedEntryCount ?? 0) == 0, $"put_targets unexpectedly failed: FailedEntryCount={resp.FailedEntryCount}");
        var listed = await ev.ListTargetsByRuleAsync(new ListTargetsByRuleRequest { Rule = rule, EventBusName = bus });
        var byId = (listed.Targets ?? []).ToDictionary(t => t.Id);
        Ensure(byId.ContainsKey("t1"), $"expected target t1 in ListTargetsByRule, got [{string.Join(", ", byId.Keys.Select(k => $"'{k}'"))}]");
        Ensure(byId["t1"].Arn == targetArn, $"expected Arn {targetArn}, got {byId["t1"].Arn}");
    }

    /// <summary>Syntactic Lambda target ARN in ref_arn's partition/region/account (EventBridge does not verify existence).</summary>
    private static string LambdaTargetArn(string refArn)
    {
        var p = refArn.Split(':');
        return $"arn:{p[1]}:lambda:{p[3]}:{p[4]}:function:sim2real-behav-fn-{Guid.NewGuid().ToString("N")[..8]}";
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
