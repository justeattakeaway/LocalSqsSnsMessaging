// Translated from behavioral/events/check_put_rule_schedule_expression_grammar_rejection.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.EventBridge;
using Amazon.EventBridge.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Events;

/// <summary>events.put-rule-schedule-expression-grammar-rejection</summary>
[Category("AwsEvaluationSuite")]
public sealed class PutRuleScheduleExpressionGrammarRejection
{
    private const string Check = "put-rule-schedule-expression-grammar-rejection";

    /// <summary>
    /// PutRule rejects malformed rate()/cron() expressions (ValidationException), creating no rule.
    ///
    /// Valid forms are accepted and round-trip exactly through DescribeRule and ListRules.
    ///
    /// Reference: https://docs.aws.amazon.com/eventbridge/latest/userguide/eb-scheduled-rule-pattern.html
    /// </summary>
    [Test]
    [DisplayName("events.put-rule-schedule-expression-grammar-rejection")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ev = ctx.Events;

        // --- refusals, each with effect-absent ------------------------------------------------
        foreach (var (expression, why) in new[]
        {
            ("rate(1 minutes)", "a singular rate value with a plural unit"),
            ("rate(5 minute)", "a plural rate value with a singular unit"),
            ("rate(0 minutes)", "a rate value of zero"),
            ("rate(30 seconds)", "a rate unit finer than one minute"),
            ("rate(1 fortnight)", "a rate unit outside minute/hour/day"),
            ("cron(0 12 * * ?)", "a five-field cron expression -- the AWS dialect requires six"),
            ("cron(0 12 * * * *)", "a cron expression with `*` in BOTH day-of-month and day-of-week"),
        })
        {
            var name = FidelityContext.UniqueName("sim2real-behav-");
            var (code, message) = await Error(() => ev.PutRuleAsync(new PutRuleRequest
            {
                Name = name,
                ScheduleExpression = expression,
                State = RuleState.ENABLED,
            }));
            // ValidationException is not a modeled PutRule error, but it is what EventBridge returns.
            Ensure(
                code == "ValidationException",
                $"PutRule must reject {why} ('{expression}'): expected ValidationException, got {code}. " +
                "If the real code is InvalidEventPatternException or InvalidParameterValueException, " +
                "this assertion is the thing to correct -- see the check docstring.");
            Ensure(
                message == "Parameter ScheduleExpression is not valid.",
                $"PutRule must name the invalid ScheduleExpression for '{expression}', got '{message}'");
            // Effect absent: a refused PutRule must not have created a rule under that name.
            var listed = ((await ev.ListRulesAsync(new ListRulesRequest { NamePrefix = name })).Rules ?? []).Select(r => r.Name).ToList();
            Ensure(
                listed.Count == 0,
                $"PutRule rejected '{expression}' but ListRules(NamePrefix='{name}') returned {Repr(listed)}; " +
                "a refused create must leave no rule behind");
            var (describeCode, describeMessage) = await Error(() => ev.DescribeRuleAsync(new DescribeRuleRequest { Name = name }));
            Ensure(
                describeCode == "ResourceNotFoundException",
                $"PutRule rejected '{expression}' but DescribeRule('{name}') returned {describeCode} " +
                "instead of ResourceNotFoundException");
            Ensure(
                describeMessage == $"Rule {name} does not exist on EventBus default.",
                $"DescribeRule('{name}') must name the missing rule, got '{describeMessage}'");
        }

        // --- positive control: the valid forms are accepted and round-trip exactly -------------
        foreach (var expression in new[]
        {
            "rate(1 minute)",
            "rate(5 minutes)",
            "rate(1 hour)",
            "rate(2 days)",
            "cron(15 10 * * ? *)",          // 10:15 UTC daily, day-of-week elided with ?
            "cron(0 18 ? * MON-FRI *)",     // weekdays, day-of-month elided with ?
            "cron(0 0 1 * ? *)",            // first of every month
        })
        {
            var name = FidelityContext.UniqueName("sim2real-behav-");
            await ev.PutRuleAsync(new PutRuleRequest
            {
                Name = name,
                ScheduleExpression = expression,
                State = RuleState.DISABLED,
                Description = "sim2real behavioral: schedule-expression grammar",
                Tags = [new Tag { Key = "sim2real", Value = Check }],
            });
            ctx.OnCleanup(() => ev.DeleteRuleAsync(new DeleteRuleRequest { Name = name }));
            var described = await ev.DescribeRuleAsync(new DescribeRuleRequest { Name = name });
            Ensure(
                described.ScheduleExpression == expression,
                "DescribeRule must round-trip the schedule expression byte-identically: " +
                $"stored '{described.ScheduleExpression}', submitted '{expression}'");
            // A scheduled rule carries no EventPattern, and the requested State must be honoured
            // rather than defaulted to ENABLED.
            Ensure(
                described.EventPattern is null,
                $"a schedule-only rule must have no EventPattern, got '{described.EventPattern}'");
            Ensure(described.State?.Value == "DISABLED", $"PutRule must honour State=DISABLED, got '{described.State?.Value}'");
        }

        // A rule created as DISABLED must be reported as DISABLED by the list API too, not just by
        // DescribeRule -- the two read paths must agree.
        var lastName = FidelityContext.UniqueName("sim2real-behav-");
        await ev.PutRuleAsync(new PutRuleRequest
        {
            Name = lastName,
            ScheduleExpression = "rate(1 hour)",
            State = RuleState.DISABLED,
            Tags = [new Tag { Key = "sim2real", Value = Check }],
        });
        ctx.OnCleanup(() => ev.DeleteRuleAsync(new DeleteRuleRequest { Name = lastName }));
        var rules = ((await ev.ListRulesAsync(new ListRulesRequest { NamePrefix = lastName })).Rules ?? [])
            .Where(r => r.Name == lastName).ToList();
        Ensure(
            rules.Count == 1,
            $"expected exactly one rule named '{lastName}' from ListRules, got " +
            $"[{string.Join(", ", rules.Select(r => $"\"{r.Name}\""))}]");
        Ensure(
            rules[0].State?.Value == "DISABLED",
            $"ListRules reported State='{rules[0].State?.Value}' for a rule DescribeRule calls DISABLED");
        Ensure(
            rules[0].ScheduleExpression == "rate(1 hour)",
            $"ListRules reported ScheduleExpression='{rules[0].ScheduleExpression}'");
    }

    /// <summary>Run `call` and return the AWS error (code, message) it raised; raise if it succeeds.</summary>
    private static async Task<(string Code, string Message)> Error<T>(Func<Task<T>> call)
        where T : class
    {
        var (_, error) = await FidelityContext.Capture(call);
        if (error is not null)
        {
            return (error.Code ?? "<missing Error.Code>", error.Message ?? "");
        }

        throw Fail("expected a ClientError, but the call SUCCEEDED");
    }

    private static string Repr(IEnumerable<string> items) => "[" + string.Join(", ", items.Select(i => $"'{i}'")) + "]";
}
