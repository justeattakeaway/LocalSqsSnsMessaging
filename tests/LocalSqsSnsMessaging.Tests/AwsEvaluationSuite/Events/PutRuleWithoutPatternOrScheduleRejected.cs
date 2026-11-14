// Translated from behavioral/events/check_put_rule_without_pattern_or_schedule_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.EventBridge.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Events;

/// <summary>events.put-rule-without-pattern-or-schedule-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class PutRuleWithoutPatternOrScheduleRejected
{
    private const string Check = "put-rule-without-pattern-or-schedule-rejected";

    /// <summary>
    /// PutRule with neither EventPattern nor ScheduleExpression is rejected with ValidationException
    /// and creates no rule, while a PutRule with an EventPattern succeeds as a positive control.
    /// </summary>
    [Test]
    [DisplayName("events.put-rule-without-pattern-or-schedule-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    [KnownFidelityGap(FidelityGap.EventBridgeValidation, "PutRule with neither EventPattern nor ScheduleExpression succeeds; AWS refuses it with ValidationException")]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ev = ctx.Events;

        var controlName = FidelityContext.UniqueName("sim2real-behav-rule-ctl-");
        var control = await ev.PutRuleAsync(new PutRuleRequest
        {
            Name = controlName,
            EventPattern = """{"source": ["sim2real.control"]}""",
            Tags = [new Tag { Key = "sim2real-check", Value = Check }],
        });
        ctx.OnCleanup(() => ev.DeleteRuleAsync(new DeleteRuleRequest { Name = controlName }));
        Ensure(!string.IsNullOrEmpty(control.RuleArn), "positive control: PutRule with a real EventPattern must succeed");

        var name = FidelityContext.UniqueName("sim2real-behav-rule-");
        var err = await ClientError(() => ev.PutRuleAsync(new PutRuleRequest { Name = name }));
        var code = err.Code;
        Ensure(
            code == "ValidationException",
            $"PutRule with no EventPattern and no ScheduleExpression must fail with ValidationException, got '{code}' ('{err.Message}')");
        const string expectedMessage = "Parameter(s) EventPattern or ScheduleExpression must be specified.";
        Ensure(err.Message == expectedMessage, $"expected message '{expectedMessage}', got '{err.Message}'");

        var err2 = await ClientError(() => ev.DescribeRuleAsync(new DescribeRuleRequest { Name = name }));
        var code2 = err2.Code;
        Ensure(code2 == "ResourceNotFoundException", $"rejected PutRule must not create a rule, describe gave '{code2}'");
        var expectedDescribeMessage = $"Rule {name} does not exist on EventBus default.";
        Ensure(err2.Message == expectedDescribeMessage, $"expected message '{expectedDescribeMessage}', got '{err2.Message}'");
    }

    /// <summary>Botocore Error dict of a call that must fail; an unexpected success raises.</summary>
    private static async Task<AwsError> ClientError<T>(Func<Task<T>> fn)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        return error ?? throw Fail($"real AWS rejects this call, but it SUCCEEDED and returned {result}");
    }
}
