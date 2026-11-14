// Translated from behavioral/events/check_remove_targets_unknown_id_is_lenient.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.EventBridge;
using Amazon.EventBridge.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Events;

/// <summary>events.remove-targets-unknown-id-is-lenient</summary>
[Category("AwsEvaluationSuite")]
public sealed class RemoveTargetsUnknownIdIsLenient
{
    /// <summary>
    /// RemoveTargets silently ignores unknown target Ids (FailedEntryCount 0, no FailedEntries) and
    /// still removes the known Id in the same call; repeating the removal is idempotent.
    /// </summary>
    [Test]
    [DisplayName("events.remove-targets-unknown-id-is-lenient")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    [KnownFidelityGap(FidelityGap.EventBridgeListing, "ListTargetsByRule of a rule with no targets omits the Targets member (KeyError 'Targets'); AWS returns \"Targets\": []")]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ev = ctx.Events;
        var ruleName = FidelityContext.UniqueName("sim2real-behav-evb-rule-");
        await ev.PutRuleAsync(new PutRuleRequest
        {
            Name = ruleName,
            EventPattern = """{"source": ["x"]}""",
            Tags = [new Tag { Key = "sim2real", Value = "events-behavioral" }],
        });
        ctx.OnCleanup(() => CleanupRule(ev, ruleName));

        var targetArn = await SyntheticQueueArn(ev);
        await ev.PutTargetsAsync(new PutTargetsRequest { Rule = ruleName, Targets = [new Target { Id = "kept", Arn = targetArn }] });

        var resp = await ev.RemoveTargetsAsync(new RemoveTargetsRequest { Rule = ruleName, Ids = ["kept", "never-existed"] });
        Ensure(resp.FailedEntryCount == 0, $"unknown Id must not be reported as failed: {Show(resp)}");
        Ensure((resp.FailedEntries ?? []).Count == 0, $"expected no FailedEntries, got {Show(resp)}");

        var remaining = await Targets(ev, ruleName);
        Ensure(remaining.Count == 0, $"the existing 'kept' target should have been removed: {Ids(remaining)}");

        var again = await ev.RemoveTargetsAsync(new RemoveTargetsRequest { Rule = ruleName, Ids = ["kept"] });
        Ensure(again.FailedEntryCount == 0, $"repeat removal must be idempotent: {Show(again)}");

        var stillRemaining = await Targets(ev, ruleName);
        Ensure(stillRemaining.Count == 0, $"repeat removal must not resurrect or alter targets: {Ids(stillRemaining)}");
    }

    // Python indexes list_targets_by_rule(...)["Targets"], which raises KeyError when the response omits it.
    private static async Task<List<Target>> Targets(AmazonEventBridgeClient ev, string rule) =>
        (await ev.ListTargetsByRuleAsync(new ListTargetsByRuleRequest { Rule = rule })).Targets
            ?? throw new KeyNotFoundException("'Targets'");

    /// <summary>Remove any attached targets, then delete the rule (DeleteRule refuses a rule with targets).</summary>
    private static async Task CleanupRule(AmazonEventBridgeClient ev, string name)
    {
        var error = await FidelityContext.CaptureError(async () =>
        {
            var targets = (await ev.ListTargetsByRuleAsync(new ListTargetsByRuleRequest { Rule = name })).Targets ?? [];
            if (targets.Count > 0)
            {
                await ev.RemoveTargetsAsync(new RemoveTargetsRequest { Rule = name, Ids = [.. targets.Select(t => t.Id)] });
            }
        });
        if (error is not null && error.Code != "ResourceNotFoundException")
        {
            throw error.Exception;
        }

        await ev.DeleteRuleAsync(new DeleteRuleRequest { Name = name, Force = true });
    }

    /// <summary>
    /// A syntactically valid SQS queue ARN in this account/region.
    /// EventBridge accepts target ARNs without requiring the target resource to exist.
    /// </summary>
    private static async Task<string> SyntheticQueueArn(AmazonEventBridgeClient ev)
    {
        var busArn = (await ev.DescribeEventBusAsync(new DescribeEventBusRequest { Name = "default" })).Arn;
        var parts = busArn.Split(':');
        var (partition, region, account) = (parts[1], parts[3], parts[4]);
        return $"arn:{partition}:sqs:{region}:{account}:" + FidelityContext.UniqueName("sim2real-behav-evb-target-");
    }

    private static string Show(RemoveTargetsResponse r) =>
        $"{{'FailedEntryCount': {r.FailedEntryCount}, 'FailedEntries': [{string.Join(", ", (r.FailedEntries ?? []).Select(e => $"{{'TargetId': '{e.TargetId}', 'ErrorCode': '{e.ErrorCode}'}}"))}]}}";

    private static string Ids(List<Target> targets) => "[" + string.Join(", ", targets.Select(t => $"{{'Id': '{t.Id}', 'Arn': '{t.Arn}'}}")) + "]";
}
