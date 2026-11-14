// Translated from behavioral/events/check_put_target_role_retry_policy_and_dead_letter_config_roundtrip_then_role_replaced.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.EventBridge;
using Amazon.EventBridge.Model;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Events;

// RoleArn is supplied on every PutTargets call: a Step Functions target that omits it is rejected.

/// <summary>events.put-target-role-retry-policy-and-dead-letter-config-roundtrip-then-role-replaced</summary>
[Category("AwsEvaluationSuite")]
public sealed class PutTargetRoleRetryPolicyAndDeadLetterConfigRoundtripThenRoleReplaced
{
    private static RetryPolicy NewRetryPolicy() => new() { MaximumRetryAttempts = 2, MaximumEventAgeInSeconds = 60 };

    /// <summary>
    /// A Step Functions target's RoleArn, RetryPolicy and DeadLetterConfig read back as written, and
    /// a later PutTargets on the same Id with a new RoleArn replaces the role in place, leaving one
    /// target and the RetryPolicy and DeadLetterConfig unchanged.
    /// </summary>
    [Test]
    [DisplayName("events.put-target-role-retry-policy-and-dead-letter-config-roundtrip-then-role-replaced")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var events = ctx.Events;
        var sqs = ctx.Sqs;
        var account = ctx.Account;

        // IAM is not emulated: the roles are only ARNs on the targets (no CreateRole/DeleteRole).
        var role1Arn = ctx.RoleArn(FidelityContext.UniqueName("sim2real-behav-role1-"));
        var role2Arn = ctx.RoleArn(FidelityContext.UniqueName("sim2real-behav-role2-"));

        var dlqUrl = (await sqs.CreateQueueAsync(new CreateQueueRequest { QueueName = FidelityContext.UniqueName("sim2real-behav-dlq-") })).QueueUrl;
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(dlqUrl));
        var dlqArn = (await sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest { QueueUrl = dlqUrl, AttributeNames = ["QueueArn"] })).Attributes["QueueArn"];

        var ruleName = FidelityContext.UniqueName("sim2real-behav-rule-");
        var pattern = $$"""{"source": ["{{FidelityContext.UniqueName("sim2real.roletrip.")}}"]}""";
        await events.PutRuleAsync(new PutRuleRequest { Name = ruleName, EventPattern = pattern });
        ctx.OnCleanup(() => events.DeleteRuleAsync(new DeleteRuleRequest { Name = ruleName }));
        ctx.OnCleanup(() => RemoveTargetsQuietly(events, ruleName));

        var smArn = $"arn:aws:states:{ctx.Region}:{account}:stateMachine:{FidelityContext.UniqueName("sim2real-behav-sm-")}";

        await PutTargetsWithRetry(ctx, events, ruleName, new Target
        {
            Id = "t1",
            Arn = smArn,
            RoleArn = role1Arn,
            RetryPolicy = NewRetryPolicy(),
            DeadLetterConfig = new DeadLetterConfig { Arn = dlqArn },
        });
        var first = await OnlyTarget(events, ruleName);
        Ensure(first.RoleArn == role1Arn, $"expected RoleArn '{role1Arn}', got '{first.RoleArn}'");
        Ensure(IsRetryPolicy(first.RetryPolicy), $"expected RetryPolicy {Show(NewRetryPolicy())}, got {Show(first.RetryPolicy)}");
        Ensure(first.DeadLetterConfig?.Arn == dlqArn, $"expected DeadLetterConfig arn '{dlqArn}', got {Show(first.DeadLetterConfig)}");

        await PutTargetsWithRetry(ctx, events, ruleName, new Target
        {
            Id = "t1",
            Arn = smArn,
            RoleArn = role2Arn,
            RetryPolicy = NewRetryPolicy(),
            DeadLetterConfig = new DeadLetterConfig { Arn = dlqArn },
        });
        var targetsAfter = (await events.ListTargetsByRuleAsync(new ListTargetsByRuleRequest { Rule = ruleName })).Targets
            ?? throw new KeyNotFoundException("'Targets'");
        Ensure(targetsAfter.Count == 1, $"expected exactly one target after the replace, got {targetsAfter.Count} target(s)");
        var second = targetsAfter[0];
        Ensure(second.RoleArn == role2Arn, $"expected the REPLACED RoleArn '{role2Arn}', got '{second.RoleArn}'");
        Ensure(second.RoleArn != first.RoleArn, "the role must actually have changed between the two calls");
        Ensure(IsRetryPolicy(second.RetryPolicy), $"RetryPolicy must survive the role replace unchanged; got {Show(second.RetryPolicy)}");
        Ensure(second.DeadLetterConfig?.Arn == dlqArn, $"DeadLetterConfig must survive the role replace unchanged; got {Show(second.DeadLetterConfig)}");
    }

    private static bool IsRetryPolicy(RetryPolicy? p) => p is { MaximumRetryAttempts: 2, MaximumEventAgeInSeconds: 60 };

    /// <summary>
    /// IAM role propagation has no condition to poll, so retry, bounded, on the specific
    /// 'cannot be assumed' error.
    /// </summary>
    private static async Task PutTargetsWithRetry(FidelityContext ctx, AmazonEventBridgeClient events, string rule, Target target, double timeoutS = 80.0)
    {
        AwsError? last = null;
        var started = FidelityTime.StartNew();
        var limit = TimeSpan.FromSeconds(Math.Min(timeoutS, ctx.TimeLeftSeconds));
        while (started.Elapsed < limit)
        {
            var (resp, error) = await FidelityContext.Capture(() => events.PutTargetsAsync(new PutTargetsRequest { Rule = rule, Targets = [target] }));
            if (error is null)
            {
                Ensure(resp!.FailedEntryCount == 0, $"PutTargets failed entries: {resp.FailedEntries?.Count}");
                return;
            }

            if (!$"{error.Code} {error.Message}".Contains("cannot be assumed", StringComparison.Ordinal))
            {
                throw error.Exception;
            }

            last = error;
            await FidelityTime.Delay(TimeSpan.FromSeconds(2));
        }

        throw Fail($"PutTargets never accepted the role within {timeoutS}s: {last}");
    }

    private static async Task<Target> OnlyTarget(AmazonEventBridgeClient events, string rule)
    {
        var targets = (await events.ListTargetsByRuleAsync(new ListTargetsByRuleRequest { Rule = rule })).Targets
            ?? throw new KeyNotFoundException("'Targets'");
        Ensure(targets.Count == 1, $"expected exactly one target on '{rule}', got {targets.Count} target(s)");
        return targets[0];
    }

    /// <summary>
    /// DeleteRule rejects a rule that still has targets (``Force`` only waives that check for a
    /// rule a service created on your behalf, never for one you own) -- remove them first.
    /// </summary>
    private static async Task RemoveTargetsQuietly(AmazonEventBridgeClient events, string rule)
    {
        var ids = ((await events.ListTargetsByRuleAsync(new ListTargetsByRuleRequest { Rule = rule })).Targets ?? []).Select(t => t.Id).ToList();
        if (ids.Count > 0)
        {
            await events.RemoveTargetsAsync(new RemoveTargetsRequest { Rule = rule, Ids = ids });
        }
    }

    private static string Show(RetryPolicy? p) =>
        p is null ? "None" : $"{{'MaximumRetryAttempts': {p.MaximumRetryAttempts}, 'MaximumEventAgeInSeconds': {p.MaximumEventAgeInSeconds}}}";

    private static string Show(DeadLetterConfig? d) => d is null ? "None" : $"{{'Arn': '{d.Arn}'}}";
}
