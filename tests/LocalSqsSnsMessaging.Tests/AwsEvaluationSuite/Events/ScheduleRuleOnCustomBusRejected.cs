// Translated from behavioral/events/check_schedule_rule_on_custom_bus_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.EventBridge.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Events;

/// <summary>events.schedule-rule-on-custom-bus-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class ScheduleRuleOnCustomBusRejected
{
    private static List<Tag> TagList() => [new Tag { Key = "sim2real", Value = "behavioral" }];

    /// <summary>
    /// PutRule with a ScheduleExpression on a custom bus is rejected with ValidationException
    /// (schedules are default-bus only) and creates no rule; a control pattern rule on that bus is
    /// created first and stays ENABLED, so the rejection is about the schedule.
    /// </summary>
    [Test]
    [DisplayName("events.schedule-rule-on-custom-bus-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ev = ctx.Events;
        var bus = FidelityContext.UniqueName("sim2real-behav-bus-");
        await ev.CreateEventBusAsync(new CreateEventBusRequest { Name = bus, Tags = TagList() });
        ctx.OnCleanup(() => ev.DeleteEventBusAsync(new DeleteEventBusRequest { Name = bus }));

        var controlName = FidelityContext.UniqueName("sim2real-behav-rule-control-");
        await ev.PutRuleAsync(new PutRuleRequest
        {
            Name = controlName,
            EventPattern = """{"source": ["sim2real.control"]}""",
            EventBusName = bus,
            Tags = TagList(),
        });
        ctx.OnCleanup(() => ev.DeleteRuleAsync(new DeleteRuleRequest { Name = controlName, EventBusName = bus, Force = true }));
        var controlDesc = await ev.DescribeRuleAsync(new DescribeRuleRequest { Name = controlName, EventBusName = bus });
        Ensure(controlDesc.Name == controlName, $"positive control rule did not register on the custom bus: {Show(controlDesc)}");

        var ruleName = FidelityContext.UniqueName("sim2real-behav-rule-");
        var error = await ClientError(() => ev.PutRuleAsync(new PutRuleRequest
        {
            Name = ruleName,
            ScheduleExpression = "rate(5 minutes)",
            EventBusName = bus,
            Tags = TagList(),
        }));
        Ensure(
            error.Code == "ValidationException",
            $"scheduled rule on a custom event bus was rejected with '{error.Code}', expected 'ValidationException': {error}");
        const string expectedMessage = "ScheduleExpression is supported only on the default event bus.";
        Ensure(error.Message == expectedMessage, $"expected message '{expectedMessage}', got '{error.Message}'");

        var rules = (await ev.ListRulesAsync(new ListRulesRequest { NamePrefix = ruleName, EventBusName = bus })).Rules
            ?? throw new KeyNotFoundException("'Rules'");
        Ensure(rules.Count == 0, $"rejected scheduled rule must not be created: [{string.Join(", ", rules.Select(r => $"'{r.Name}'"))}]");

        var stillThere = await ev.DescribeRuleAsync(new DescribeRuleRequest { Name = controlName, EventBusName = bus });
        Ensure(stillThere.State?.Value == "ENABLED", $"positive control rule was affected by the rejected call: {Show(stillThere)}");
    }

    /// <summary>Call fn() and return its botocore Error dict; an unexpected success raises.</summary>
    private static async Task<AwsError> ClientError<T>(Func<Task<T>> fn)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        return error ?? throw Fail($"expected PutRule to be rejected, but it SUCCEEDED and returned {result}");
    }

    private static string Show(DescribeRuleResponse d) =>
        $"{{'Name': '{d.Name}', 'Arn': '{d.Arn}', 'EventBusName': '{d.EventBusName}', 'State': '{d.State?.Value}'}}";
}
