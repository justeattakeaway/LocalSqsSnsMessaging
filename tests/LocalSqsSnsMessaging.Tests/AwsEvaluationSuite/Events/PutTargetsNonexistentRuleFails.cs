// Translated from behavioral/events/check_put_targets_nonexistent_rule_fails.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.EventBridge;
using Amazon.EventBridge.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Events;

/// <summary>events.put-targets-nonexistent-rule-fails</summary>
[Category("AwsEvaluationSuite")]
public sealed class PutTargetsNonexistentRuleFails
{
    private const string Check = "put-targets-nonexistent-rule-fails";

    /// <summary>
    /// OVERLY-PERMISSIVE: PutTargets against a nonexistent rule must fail, must not
    /// register a residual target that later appears once a same-named rule is created,
    /// and the identical call must succeed once that rule genuinely exists.
    /// </summary>
    [Test]
    [DisplayName("events.put-targets-nonexistent-rule-fails")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    [KnownFidelityGap(FidelityGap.EventBridgeErrorMessages, "PutTargets on a missing rule says '... does not exist on event bus <bus>.' instead of '... on EventBus <bus>.'")]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ev = ctx.Events;
        var bus = FidelityContext.UniqueName("sim2real-behav-bus-");
        var busResp = await ev.CreateEventBusAsync(new CreateEventBusRequest { Name = bus, Tags = [new Tag { Key = "sim2real-check", Value = Check }] });
        ctx.OnCleanup(() => ev.DeleteEventBusAsync(new DeleteEventBusRequest { Name = bus }));
        var rule = FidelityContext.UniqueName("sim2real-behav-rule-");
        var targetArn = LambdaTargetArn(busResp.EventBusArn);

        // Positive control: PutTargets against a real rule on the same bus succeeds,
        // so a rejection below cannot be indistinguishable from "rejects every PutTargets".
        var controlRule = FidelityContext.UniqueName("sim2real-behav-rule-ctl-");
        await ev.PutRuleAsync(new PutRuleRequest
        {
            Name = controlRule,
            EventBusName = bus,
            EventPattern = """{"source": ["sim2real.control"]}""",
            Tags = [new Tag { Key = "sim2real-check", Value = Check }],
        });
        ctx.OnCleanup(() => CleanupRule(ev, controlRule, bus));
        await ev.PutTargetsAsync(new PutTargetsRequest { Rule = controlRule, EventBusName = bus, Targets = [new Target { Id = "ctl", Arn = targetArn }] });
        var controlTargets = await Targets(ev, controlRule, bus);
        Ensure(controlTargets.Count == 1, $"expected positive control to register 1 target, got {Show(controlTargets)}");

        // Break: `rule` has never been created — PutTargets against it must fail.
        var (result, err) = await FidelityContext.Capture(() =>
            ev.PutTargetsAsync(new PutTargetsRequest { Rule = rule, EventBusName = bus, Targets = [new Target { Id = "t1", Arn = targetArn }] }));
        if (err is null)
        {
            throw Fail($"PutTargets against a nonexistent rule SUCCEEDED: {result}");
        }

        Ensure(err.Code == "ResourceNotFoundException", $"expected ResourceNotFoundException, got {err}");
        var expectedMessage = $"Rule {rule} does not exist on EventBus {bus}.";
        Ensure(err.Message == expectedMessage, $"expected message '{expectedMessage}', got '{err.Message}'");

        // Restore: create the identically-named rule, and prove the earlier rejected call
        // left no residual target queued for it — an independent read at the destination.
        await ev.PutRuleAsync(new PutRuleRequest
        {
            Name = rule,
            EventBusName = bus,
            EventPattern = """{"source": ["sim2real.restored"]}""",
            Tags = [new Tag { Key = "sim2real-check", Value = Check }],
        });
        ctx.OnCleanup(() => CleanupRule(ev, rule, bus));
        var residual = await Targets(ev, rule, bus);
        Ensure(residual.Count == 0, $"expected no residual target on newly-created rule, got {Show(residual)}");

        // The identical call now succeeds against the restored rule.
        await ev.PutTargetsAsync(new PutTargetsRequest { Rule = rule, EventBusName = bus, Targets = [new Target { Id = "t1", Arn = targetArn }] });
        var restoredTargets = await Targets(ev, rule, bus);
        Ensure(
            restoredTargets.Count == 1 && restoredTargets[0].Id == "t1",
            $"expected target t1 registered after restore, got {Show(restoredTargets)}");
    }

    /// <summary>Syntactic Lambda target ARN in ref_arn's partition/region/account (EventBridge does not verify existence).</summary>
    private static string LambdaTargetArn(string refArn)
    {
        var p = refArn.Split(':');
        return $"arn:{p[1]}:lambda:{p[3]}:{p[4]}:function:sim2real-behav-fn-{Guid.NewGuid().ToString("N")[..8]}";
    }

    /// <summary>Teardown: DeleteRule refuses a rule that still has targets, so remove them first.</summary>
    private static async Task CleanupRule(AmazonEventBridgeClient ev, string name, string bus)
    {
        var ids = ((await ev.ListTargetsByRuleAsync(new ListTargetsByRuleRequest { Rule = name, EventBusName = bus })).Targets ?? [])
            .Select(t => t.Id).ToList();
        if (ids.Count > 0)
        {
            await ev.RemoveTargetsAsync(new RemoveTargetsRequest { Rule = name, EventBusName = bus, Ids = ids, Force = true });
        }

        await ev.DeleteRuleAsync(new DeleteRuleRequest { Name = name, EventBusName = bus, Force = true });
    }

    // Python indexes list_targets_by_rule(...)["Targets"], which raises KeyError when the response omits it.
    private static async Task<List<Target>> Targets(AmazonEventBridgeClient ev, string rule, string bus) =>
        (await ev.ListTargetsByRuleAsync(new ListTargetsByRuleRequest { Rule = rule, EventBusName = bus })).Targets
            ?? throw new KeyNotFoundException("'Targets'");

    private static string Show(List<Target> targets) => "[" + string.Join(", ", targets.Select(t => $"{{'Id': '{t.Id}', 'Arn': '{t.Arn}'}}")) + "]";
}
