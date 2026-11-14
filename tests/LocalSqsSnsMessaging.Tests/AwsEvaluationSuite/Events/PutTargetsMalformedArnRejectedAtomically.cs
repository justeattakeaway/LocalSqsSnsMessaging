// Translated from behavioral/events/check_put_targets_malformed_arn_rejected_atomically.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.EventBridge;
using Amazon.EventBridge.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Events;

/// <summary>events.put-targets-malformed-arn-rejected-atomically</summary>
[Category("AwsEvaluationSuite")]
public sealed class PutTargetsMalformedArnRejectedAtomically
{
    /// <summary>
    /// A malformed target ARN fails the whole PutTargets call with ValidationException rather than
    /// a per-target FailedEntry, and the well-formed sibling target in the same batch is not added.
    /// </summary>
    [Test]
    [DisplayName("events.put-targets-malformed-arn-rejected-atomically")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ev = ctx.Events;
        var ruleName = FidelityContext.UniqueName("sim2real-behav-evb-rule-");
        await ev.PutRuleAsync(new PutRuleRequest
        {
            Name = ruleName,
            EventPattern = """{"source": ["x"]}""",
            Tags = [new Tag { Key = "sim2real-behav", Value = "check_put_targets_malformed_arn_rejected_atomically" }],
        });
        ctx.OnCleanup(() => CleanupRule(ev, ruleName));

        var targetArn = await SyntheticQueueArn(ev);

        var err = await ClientError(() => ev.PutTargetsAsync(new PutTargetsRequest
        {
            Rule = ruleName,
            Targets =
            [
                new Target { Id = "good-target", Arn = targetArn },
                new Target { Id = "bad-target", Arn = "not-a-valid-arn" },
            ],
        }));
        Ensure(err.Code == "ValidationException", $"expected ValidationException, got {err}");
        const string expectedMessage = "Parameter not-a-valid-arn is not valid. Reason: Provided Arn is not in correct format.";
        Ensure(err.Message == expectedMessage, $"expected message '{expectedMessage}', got '{err.Message}'");

        var targets = await Targets(ev, ruleName);
        Ensure(targets.Count == 0, $"a rejected PutTargets must not partially apply, but found {Show(targets)}");

        // The same batch minus the malformed ARN succeeds, proving the rejection was
        // caused by the ARN and not by the batch shape.
        var ok = await ev.PutTargetsAsync(new PutTargetsRequest { Rule = ruleName, Targets = [new Target { Id = "good-target", Arn = targetArn }] });
        Ensure(ok.FailedEntryCount == 0, $"well-formed target should be accepted: FailedEntryCount={ok.FailedEntryCount}");
        var ids = (await Targets(ev, ruleName)).Select(t => t.Id).ToHashSet();
        Ensure(ids.SetEquals(["good-target"]), $"expected only 'good-target' attached, got {{{string.Join(", ", ids.Select(i => $"'{i}'"))}}}");
    }

    /// <summary>Call fn() and return the botocore Error dict it raised; an unexpected success raises.</summary>
    private static async Task<AwsError> ClientError<T>(Func<Task<T>> fn)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        return error ?? throw Fail($"expected this call to be rejected, but it SUCCEEDED and returned {result}");
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

    private static string Show(List<Target> targets) => "[" + string.Join(", ", targets.Select(t => $"{{'Id': '{t.Id}', 'Arn': '{t.Arn}'}}")) + "]";
}
