// Translated from behavioral/events/check_schedule_rule_on_default_bus_accepted.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.EventBridge.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Events;

/// <summary>events.schedule-rule-on-default-bus-accepted</summary>
[Category("AwsEvaluationSuite")]
public sealed class ScheduleRuleOnDefaultBusAccepted
{
    private const string Check = "schedule-rule-on-default-bus-accepted";

    /// <summary>
    /// A valid ScheduleExpression rule IS accepted on the default bus and DescribeRule echoes the expression back with no EventPattern.
    /// </summary>
    [Test]
    [DisplayName("events.schedule-rule-on-default-bus-accepted")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ev = ctx.Events;
        var ruleName = FidelityContext.UniqueName("sim2real-behav-rule-");
        await ev.PutRuleAsync(new PutRuleRequest
        {
            Name = ruleName,
            ScheduleExpression = "rate(5 minutes)",
            Tags = [new Tag { Key = "sim2real-check", Value = Check }],
        });
        ctx.OnCleanup(() => ev.DeleteRuleAsync(new DeleteRuleRequest { Name = ruleName, Force = true }));

        var desc = await ev.DescribeRuleAsync(new DescribeRuleRequest { Name = ruleName });
        Ensure(desc.ScheduleExpression == "rate(5 minutes)", $"unexpected schedule: {Show(desc)}");
        Ensure(string.IsNullOrEmpty(desc.EventPattern), $"schedule rule should have no EventPattern: {Show(desc)}");
        Ensure(
            desc.State?.Value == "ENABLED",
            $"PutRule with no explicit State must default to ENABLED, got '{desc.State?.Value}': {Show(desc)}");
    }

    private static string Show(DescribeRuleResponse d) =>
        $"{{'Name': '{d.Name}', 'ScheduleExpression': '{d.ScheduleExpression}', 'EventPattern': '{d.EventPattern}', 'State': '{d.State?.Value}'}}";
}
