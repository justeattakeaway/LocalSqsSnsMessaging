// Translated from behavioral/events/check_put_targets_omitted_role_arn_rejected_leaves_prior_target_unchanged.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.EventBridge;
using Amazon.EventBridge.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Events;

// Real AWS rejects a role-requiring target without RoleArn outright (never a silent null or a
// merge with the stored role). A cross-account bus target needs a second account, so a Step
// Functions target proves the same RoleArn-is-mandatory-and-atomic invariant.

/// <summary>events.put-targets-omitted-role-arn-rejected-leaves-prior-target-unchanged</summary>
[Category("AwsEvaluationSuite")]
public sealed class PutTargetsOmittedRoleArnRejectedLeavesPriorTargetUnchanged
{
    /// <summary>
    /// PutTargets omitting RoleArn for a role-requiring target is refused on create and on both
    /// update shapes, leaving the stored target unchanged; the rule's RoleArn is never inherited.
    /// </summary>
    [Test]
    [DisplayName("events.put-targets-omitted-role-arn-rejected-leaves-prior-target-unchanged")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    [KnownFidelityGap(FidelityGap.EventBridgeTargets, "PutTargets accepts a Step Functions target without a RoleArn; AWS refuses it with 'RoleArn is required for target <arn>.'")]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var events = ctx.Events;
        var account = ctx.Account;

        // IAM is not emulated: the roles are only ARNs on the rule and target (no CreateRole/DeleteRole).
        var ruleRoleArn = ctx.RoleArn(FidelityContext.UniqueName("sim2real-behav-rulerole-"));
        var targetRoleArn = ctx.RoleArn(FidelityContext.UniqueName("sim2real-behav-targetrole-"));

        var ruleName = FidelityContext.UniqueName("sim2real-behav-rule-");
        var pattern = $$"""{"source": ["{{FidelityContext.UniqueName("sim2real.rolearn.")}}"]}""";
        await PutRuleWithRetry(ctx, events, ruleName, pattern, ruleRoleArn);
        ctx.OnCleanup(() => events.DeleteRuleAsync(new DeleteRuleRequest { Name = ruleName }));
        ctx.OnCleanup(() => RemoveTargetsQuietly(events, ruleName));

        var smArn = $"arn:aws:states:{ctx.Region}:{account}:stateMachine:{FidelityContext.UniqueName("sim2real-behav-sm-")}";

        // 1. The very first PutTargets for this Id, with RoleArn omitted, must be refused -- not
        // accepted with a null role, and NOT silently inheriting the rule's own RoleArn.
        var (kind, code, msg) = await AttemptPutTargets(events, ruleName, new Target { Id = "t1", Arn = smArn });
        Ensure(kind == "exception", $"expected a whole-request rejection, got a FailedEntries row: '{msg}'");
        var expected = ("ValidationException", $"RoleArn is required for target {smArn}.");
        Ensure((code, msg) == expected, $"expected {Show(expected)}, got {Show((code, msg))}");
        if ((await Targets(events, ruleName)).Count != 0)
        {
            throw Fail(
                "a refused PutTargets must not create the target at all -- " +
                $"got {Show(await Targets(events, ruleName))}");
        }

        // Positive control: supplying the target's OWN RoleArn succeeds, and the stored role is the
        // target's role, not the rule's -- proving RoleArn is never inherited from the rule.
        await PutTargetsWithRetry(ctx, events, ruleName, new Target { Id = "t1", Arn = smArn, RoleArn = targetRoleArn });
        var stored = await OnlyTarget(events, ruleName);
        Ensure(
            stored.RoleArn == targetRoleArn,
            $"stored RoleArn must be the target's own role '{targetRoleArn}', not the rule's '{ruleRoleArn}'; got '{stored.RoleArn}'");

        // 2. An UPDATE that keeps the same Id and Arn but omits RoleArn must ALSO be refused, and must
        // leave the previously stored target byte-identical -- never merge in a null/absent role.
        (kind, code, msg) = await AttemptPutTargets(events, ruleName, new Target { Id = "t1", Arn = smArn });
        Ensure(kind == "exception", $"expected a whole-request rejection, got a FailedEntries row: '{msg}'");
        expected = ("ValidationException", $"RoleArn is required for target {smArn}.");
        Ensure((code, msg) == expected, $"expected {Show(expected)}, got {Show((code, msg))}");
        var afterSameArn = await OnlyTarget(events, ruleName);
        Ensure(
            Show(afterSameArn) == Show(stored),
            $"a refused same-Arn update must leave the target unchanged; before {Show(stored)}, after {Show(afterSameArn)}");

        // 3. An UPDATE that changes the Arn and still omits RoleArn must likewise be refused, and must
        // leave the ORIGINAL target (original Arn, original role) unchanged -- the role is never
        // cleared by a refused write.
        var changedArn = smArn + "-changed";
        (kind, code, msg) = await AttemptPutTargets(events, ruleName, new Target { Id = "t1", Arn = changedArn });
        Ensure(kind == "exception", $"expected a whole-request rejection, got a FailedEntries row: '{msg}'");
        expected = ("ValidationException", $"RoleArn is required for target {changedArn}.");
        Ensure((code, msg) == expected, $"expected {Show(expected)}, got {Show((code, msg))}");
        var afterChangedArn = await OnlyTarget(events, ruleName);
        Ensure(
            Show(afterChangedArn) == Show(stored),
            $"a refused Arn-changing update must leave the original target unchanged; before {Show(stored)}, after {Show(afterChangedArn)}");
    }

    /// <summary>IAM role propagation has no condition to poll; retry boundedly on 'cannot be assumed'.</summary>
    private static async Task PutRuleWithRetry(FidelityContext ctx, AmazonEventBridgeClient events, string name, string pattern, string roleArn, double timeoutS = 80.0)
    {
        AwsError? last = null;
        var started = FidelityTime.StartNew();
        var limit = TimeSpan.FromSeconds(Math.Min(timeoutS, ctx.TimeLeftSeconds));
        while (started.Elapsed < limit)
        {
            var error = await FidelityContext.CaptureError(() => events.PutRuleAsync(new PutRuleRequest { Name = name, EventPattern = pattern, RoleArn = roleArn }));
            if (error is null)
            {
                return;
            }

            if (!$"{error.Code} {error.Message}".Contains("cannot be assumed", StringComparison.Ordinal))
            {
                throw error.Exception;
            }

            last = error;
            await FidelityTime.Delay(TimeSpan.FromSeconds(2));
        }

        throw Fail($"PutRule never accepted the role within {timeoutS}s: {last}");
    }

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

    /// <summary>PutTargets expected to be refused. Returns (kind, code, message); raises if accepted.</summary>
    private static async Task<(string Kind, string? Code, string Message)> AttemptPutTargets(AmazonEventBridgeClient events, string rule, Target target)
    {
        var (resp, error) = await FidelityContext.Capture(() => events.PutTargetsAsync(new PutTargetsRequest { Rule = rule, Targets = [target] }));
        if (error is not null)
        {
            return ("exception", error.Code, error.Message ?? "");
        }

        var failed = resp!.FailedEntries ?? [];
        if (resp.FailedEntryCount is > 0)
        {
            return ("entry", failed[0].ErrorCode, failed[0].ErrorMessage ?? "");
        }

        throw Fail(
            $"PutTargets ACCEPTED a target missing a required RoleArn: {Show(target)}; response was " +
            $"{{'FailedEntryCount': {resp.FailedEntryCount}}}");
    }

    private static async Task<List<Target>> Targets(AmazonEventBridgeClient events, string rule) =>
        (await events.ListTargetsByRuleAsync(new ListTargetsByRuleRequest { Rule = rule })).Targets
            ?? throw new KeyNotFoundException("'Targets'");

    private static async Task<Target> OnlyTarget(AmazonEventBridgeClient events, string rule)
    {
        var targets = await Targets(events, rule);
        Ensure(targets.Count == 1, $"expected exactly one target on '{rule}', got {Show(targets)}");
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

    private static string Show((string? Code, string Message) e) => $"('{e.Code}', '{e.Message}')";

    private static string Show(List<Target> targets) => "[" + string.Join(", ", targets.Select(Show)) + "]";

    /// <summary>The target as a Python dict, field for field, so two reads compare as dict equality does.</summary>
    private static string Show(Target t) =>
        "{" + string.Join(", ", new[]
        {
            $"'Id': '{t.Id}'",
            $"'Arn': '{t.Arn}'",
            t.RoleArn is null ? null : $"'RoleArn': '{t.RoleArn}'",
            t.Input is null ? null : $"'Input': '{t.Input}'",
            t.InputPath is null ? null : $"'InputPath': '{t.InputPath}'",
            t.RetryPolicy is null ? null : $"'RetryPolicy': {{'MaximumRetryAttempts': {t.RetryPolicy.MaximumRetryAttempts}, 'MaximumEventAgeInSeconds': {t.RetryPolicy.MaximumEventAgeInSeconds}}}",
            t.DeadLetterConfig is null ? null : $"'DeadLetterConfig': {{'Arn': '{t.DeadLetterConfig.Arn}'}}",
        }.Where(s => s is not null)) + "}";
}
