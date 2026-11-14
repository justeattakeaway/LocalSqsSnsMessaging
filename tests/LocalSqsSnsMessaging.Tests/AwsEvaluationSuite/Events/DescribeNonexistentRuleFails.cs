// Translated from behavioral/events/check_describe_nonexistent_rule_fails.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.EventBridge.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Events;

/// <summary>events.describe-nonexistent-rule-fails</summary>
[Category("AwsEvaluationSuite")]
public sealed class DescribeNonexistentRuleFails
{
    /// <summary>
    /// DescribeRule succeeds for a rule that was actually created, but a rule that
    /// was never created raises ResourceNotFoundException.
    /// </summary>
    [Test]
    [DisplayName("events.describe-nonexistent-rule-fails")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ev = ctx.Events;

        var realRule = FidelityContext.UniqueName("sim2real-behav-rule-");
        await ev.PutRuleAsync(new PutRuleRequest { Name = realRule, EventPattern = """{"source": ["sim2real.behav"]}""" });
        ctx.OnCleanup(() => ev.DeleteRuleAsync(new DeleteRuleRequest { Name = realRule }));
        var desc = await ev.DescribeRuleAsync(new DescribeRuleRequest { Name = realRule });
        Ensure(desc.Name == realRule, $"expected positive control to describe {realRule}, got {desc.Name}");

        var fakeRule = FidelityContext.UniqueName("sim2real-behav-rule-");
        var (result, error) = await FidelityContext.Capture(() => ev.DescribeRuleAsync(new DescribeRuleRequest { Name = fakeRule }));
        if (error is null)
        {
            throw Fail($"DescribeRule on a never-created rule succeeded: {result}");
        }

        Ensure(
            error.Code == "ResourceNotFoundException" && error.Message == $"Rule {fakeRule} does not exist on EventBus default.",
            $"expected ResourceNotFoundException, got {error}");
    }
}
