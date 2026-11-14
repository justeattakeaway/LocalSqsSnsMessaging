// Translated from behavioral/events/check_put_targets_exceeds_max_limit.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.EventBridge;
using Amazon.EventBridge.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Events;

/// <summary>events.put-targets-exceeds-max-limit</summary>
[Category("AwsEvaluationSuite")]
public sealed class PutTargetsExceedsMaxLimit
{
    /// <summary>
    /// PutTargets with 6 targets, over the fixed 5-per-rule quota, fails with LimitExceededException
    /// and adds none, while the same batch minus one target is accepted.
    /// </summary>
    [Test]
    [DisplayName("events.put-targets-exceeds-max-limit")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    [KnownFidelityGap(FidelityGap.EventBridgeTargets, "PutTargets with 6 targets succeeds; AWS refuses more than 5 targets per rule with LimitExceededException")]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ev = ctx.Events;
        var ruleName = FidelityContext.UniqueName("sim2real-behav-evb-rule-");
        await ev.PutRuleAsync(new PutRuleRequest
        {
            Name = ruleName,
            EventPattern = """{"source": ["x"]}""",
            Tags = [new Tag { Key = "sim2real", Value = "behavioral" }],
        });
        ctx.OnCleanup(() => CleanupRule(ev, ruleName));

        var targetArn = await SyntheticQueueArn(ev);
        var overLimit = Enumerable.Range(0, 6).Select(i => new Target { Id = $"t-{i}", Arn = targetArn }).ToList();

        var err = await ClientError(() => ev.PutTargetsAsync(new PutTargetsRequest { Rule = ruleName, Targets = overLimit }));
        Ensure(err.Code == "LimitExceededException", $"expected LimitExceededException, got {err}");
        const string expectedMessage = "The requested resource exceeds the maximum number allowed.";
        Ensure(err.Message == expectedMessage, $"expected message '{expectedMessage}', got '{err.Message}'");

        var remaining = await Targets(ev, ruleName);
        Ensure(remaining.Count == 0, $"rejected PutTargets call must not add any targets: [{string.Join(", ", remaining.Select(t => $"'{t.Id}'"))}]");

        var atMax = overLimit.Take(5).ToList();
        var ok = await ev.PutTargetsAsync(new PutTargetsRequest { Rule = ruleName, Targets = atMax });
        Ensure(ok.FailedEntryCount == 0, $"a within-limit PutTargets should be accepted: FailedEntryCount={ok.FailedEntryCount}");
        var listed = (await Targets(ev, ruleName)).Select(t => t.Id).ToHashSet();
        Ensure(
            listed.SetEquals(atMax.Select(t => t.Id)),
            $"expected [{string.Join(", ", atMax.Select(t => $"'{t.Id}'"))}] visible, got {{{string.Join(", ", listed.Select(i => $"'{i}'"))}}}");
    }

    /// <summary>Botocore Error dict of a call that must fail; an unexpected success raises.</summary>
    private static async Task<AwsError> ClientError<T>(Func<Task<T>> fn)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        return error ?? throw Fail($"expected the over-limit PutTargets call to be rejected, but it SUCCEEDED and returned {result}");
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
    /// A valid SQS queue ARN in this account/region; EventBridge does not require the target
    /// to exist, so no queue is created.
    /// </summary>
    private static async Task<string> SyntheticQueueArn(AmazonEventBridgeClient ev)
    {
        var busArn = (await ev.DescribeEventBusAsync(new DescribeEventBusRequest { Name = "default" })).Arn;
        var parts = busArn.Split(':');
        var (partition, region, account) = (parts[1], parts[3], parts[4]);
        return $"arn:{partition}:sqs:{region}:{account}:" + FidelityContext.UniqueName("sim2real-behav-evb-target-");
    }
}
