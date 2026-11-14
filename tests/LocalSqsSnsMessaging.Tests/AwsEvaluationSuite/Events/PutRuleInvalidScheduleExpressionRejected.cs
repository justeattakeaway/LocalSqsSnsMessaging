// Translated from behavioral/events/check_put_rule_invalid_schedule_expression_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.EventBridge.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Events;

/// <summary>events.put-rule-invalid-schedule-expression-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class PutRuleInvalidScheduleExpressionRejected
{
    private const string Check = "put-rule-invalid-schedule-expression-rejected";

    /// <summary>
    /// PutRule with ScheduleExpression "rate(0 minutes)" is rejected with ValidationException and
    /// creates no rule, while "rate(5 minutes)" is accepted and round-trips as a positive control.
    /// </summary>
    [Test]
    [DisplayName("events.put-rule-invalid-schedule-expression-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    [KnownFidelityGap(FidelityGap.EventBridgeRules, "PutRule accepts ScheduleExpression 'rate(0 minutes)' instead of refusing it with 'Parameter ScheduleExpression is not valid.'")]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ev = ctx.Events;

        // Positive control: a syntactically valid ScheduleExpression is accepted and round-trips,
        // so the account/credentials are otherwise healthy for this call.
        var goodName = FidelityContext.UniqueName("sim2real-behav-rule-");
        await ev.PutRuleAsync(new PutRuleRequest { Name = goodName, ScheduleExpression = "rate(5 minutes)", Tags = [new Tag { Key = "sim2real", Value = Check }] });
        ctx.OnCleanup(() => ev.DeleteRuleAsync(new DeleteRuleRequest { Name = goodName }));
        var described = await ev.DescribeRuleAsync(new DescribeRuleRequest { Name = goodName });
        Ensure(
            (described.ScheduleExpression ?? throw new KeyNotFoundException("'ScheduleExpression'")) == "rate(5 minutes)",
            $"positive control: expected ScheduleExpression 'rate(5 minutes)', got " +
            $"'{described.ScheduleExpression}'");

        var badName = FidelityContext.UniqueName("sim2real-behav-rule-");
        var err = await ClientError(() => ev.PutRuleAsync(new PutRuleRequest { Name = badName, ScheduleExpression = "rate(0 minutes)" }));
        Ensure(
            err.Code == "ValidationException",
            $"PutRule with ScheduleExpression='rate(0 minutes)' must fail with ValidationException, " +
            $"got {err}");
        Ensure(
            err.Message == "Parameter ScheduleExpression is not valid.",
            $"expected message 'Parameter ScheduleExpression is not valid.', got '{err.Message}'");

        var listed = ((await ev.ListRulesAsync(new ListRulesRequest { NamePrefix = badName })).Rules ?? []).Select(r => r.Name).ToList();
        Ensure(
            listed.Count == 0,
            $"PutRule rejected 'rate(0 minutes)' but ListRules(NamePrefix='{badName}') returned " +
            $"[{string.Join(", ", listed.Select(n => $"'{n}'"))}]; a refused create must leave no rule behind");

        var err2 = await ClientError(() => ev.DescribeRuleAsync(new DescribeRuleRequest { Name = badName }));
        Ensure(
            err2.Code == "ResourceNotFoundException",
            $"rejected PutRule must not create a rule, describe gave {err2}");
        var expectedMessage = $"Rule {badName} does not exist on EventBus default.";
        Ensure(err2.Message == expectedMessage, $"expected message '{expectedMessage}', got '{err2.Message}'");
    }

    /// <summary>Botocore Error dict of a call that must fail; an unexpected success raises.</summary>
    private static async Task<AwsError> ClientError(Func<Task> call) =>
        await FidelityContext.CaptureError(call) ?? throw Fail("expected a ClientError, but the call SUCCEEDED");
}
