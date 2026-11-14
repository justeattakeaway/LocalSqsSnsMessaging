// Translated from behavioral/events/check_enable_nonexistent_rule_fails.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.EventBridge.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Events;

/// <summary>events.enable-nonexistent-rule-fails</summary>
[Category("AwsEvaluationSuite")]
public sealed class EnableNonexistentRuleFails
{
    /// <summary>
    /// EnableRule on a name that was never put_rule'd fails with ResourceNotFoundException,
    /// the absence holds under an independent read, and creating that same name afterward makes
    /// the identical call succeed.
    /// </summary>
    [Test]
    [DisplayName("events.enable-nonexistent-rule-fails")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ev = ctx.Events;

        // Positive control: EnableRule succeeds against a rule that really exists, so a later
        // refusal cannot be "every EnableRule call fails here" wearing a not-found costume.
        var control = FidelityContext.UniqueName("sim2real-behav-rule-control-");
        await ev.PutRuleAsync(new PutRuleRequest { Name = control, EventPattern = """{"source": ["sim2real.behav"]}""" });
        ctx.OnCleanup(() => ev.DeleteRuleAsync(new DeleteRuleRequest { Name = control }));
        await ev.EnableRuleAsync(new EnableRuleRequest { Name = control });
        var state = (await ev.DescribeRuleAsync(new DescribeRuleRequest { Name = control })).State?.Value;
        Ensure(state == "ENABLED", $"expected control rule ENABLED after EnableRule, got {state}");

        // Break exactly one thing: reference a rule name that was never created.
        var absent = FidelityContext.UniqueName("sim2real-behav-rule-absent-");
        var notFound = ("ResourceNotFoundException", $"Rule {absent} does not exist on EventBus default.");
        var err = await Error(() => ev.EnableRuleAsync(new EnableRuleRequest { Name = absent }));
        Ensure(err == notFound, $"expected {Repr(notFound)}, got {Repr(err)}");

        // Effect absent, via an independent read (DescribeRule, not EnableRule again).
        var describeErr = await Error(() => ev.DescribeRuleAsync(new DescribeRuleRequest { Name = absent }));
        Ensure(
            describeErr == notFound,
            $"expected DescribeRule to also report {Repr(notFound)} for the never-created " +
            $"rule, got {Repr(describeErr)}");

        // Restore: creating the same name now makes the identical call succeed.
        await ev.PutRuleAsync(new PutRuleRequest { Name = absent, EventPattern = """{"source": ["sim2real.behav"]}""" });
        ctx.OnCleanup(() => ev.DeleteRuleAsync(new DeleteRuleRequest { Name = absent }));
        await ev.EnableRuleAsync(new EnableRuleRequest { Name = absent });
        var restoredState = (await ev.DescribeRuleAsync(new DescribeRuleRequest { Name = absent })).State?.Value;
        Ensure(
            restoredState == "ENABLED",
            $"expected {absent} ENABLED after EnableRule once the rule exists, got {restoredState}");
    }

    /// <summary>(code, message) of a call that must fail; a call that succeeds raises AssertionError.</summary>
    private static async Task<(string? Code, string? Message)> Error(Func<Task> fn)
    {
        var error = await FidelityContext.CaptureError(fn);
        if (error is null)
        {
            throw Fail("expected a ClientError, but the call succeeded");
        }

        return (error.Code, error.Message);
    }

    private static string Repr((string? Code, string? Message) e) => $"('{e.Code}', '{e.Message}')";
}
