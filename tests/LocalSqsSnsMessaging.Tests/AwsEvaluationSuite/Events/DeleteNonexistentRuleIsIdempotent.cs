// Translated from behavioral/events/check_delete_nonexistent_rule_is_idempotent.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.EventBridge.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Events;

/// <summary>events.delete-nonexistent-rule-is-idempotent</summary>
[Category("AwsEvaluationSuite")]
public sealed class DeleteNonexistentRuleIsIdempotent
{
    /// <summary>
    /// DeleteRule on a missing rule succeeds twice, unlike DescribeRule which raises
    /// ResourceNotFoundException; it fabricates no rule and leaves the name reusable by PutRule.
    /// </summary>
    [Test]
    [DisplayName("events.delete-nonexistent-rule-is-idempotent")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ev = ctx.Events;
        var rule = FidelityContext.UniqueName("sim2real-behav-rule-");

        var expected = ("ResourceNotFoundException", $"Rule {rule} does not exist on EventBus default.");

        // DeleteRule on a missing rule succeeds: a ClientError here fails the check.
        await ev.DeleteRuleAsync(new DeleteRuleRequest { Name = rule });
        var notFound = await Err(() => ev.DescribeRuleAsync(new DescribeRuleRequest { Name = rule }));
        Ensure(
            notFound == expected,
            $"DeleteRule on a missing rule must not fabricate one, got DescribeRule error {Repr(notFound)}");

        await ev.DeleteRuleAsync(new DeleteRuleRequest { Name = rule });
        var notFound2 = await Err(() => ev.DescribeRuleAsync(new DescribeRuleRequest { Name = rule }));
        Ensure(
            notFound2 == expected,
            $"a second idempotent DeleteRule must not fabricate one either, got DescribeRule error {Repr(notFound2)}");

        // The name must still be genuinely reusable -- a phantom "already deleted"
        // tombstone must not block a later PutRule with the identical name.
        const string pattern = """{"source": ["sim2real.behav"]}""";
        await ev.PutRuleAsync(new PutRuleRequest { Name = rule, EventPattern = pattern });
        ctx.OnCleanup(() => ev.DeleteRuleAsync(new DeleteRuleRequest { Name = rule }));
        var described = await ev.DescribeRuleAsync(new DescribeRuleRequest { Name = rule });
        Ensure(
            described.EventPattern == pattern,
            $"the rule name must be reusable after the idempotent deletes, got '{described.EventPattern}'");
    }

    /// <summary>Call fn() and return its (code, message); an unexpected success raises.</summary>
    private static async Task<(string? Code, string? Message)> Err<T>(Func<Task<T>> fn)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is null)
        {
            throw Fail($"expected a ClientError, but the call SUCCEEDED and returned {result}");
        }

        return (error.Code, error.Message);
    }

    private static string Repr((string? Code, string? Message) e) => $"('{e.Code}', '{e.Message}')";
}
