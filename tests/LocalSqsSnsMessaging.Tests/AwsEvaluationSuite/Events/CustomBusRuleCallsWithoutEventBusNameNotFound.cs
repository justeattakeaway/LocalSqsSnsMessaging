// Translated from behavioral/events/check_custom_bus_rule_calls_without_event_bus_name_not_found.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.EventBridge.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Events;

/// <summary>events.custom-bus-rule-calls-without-event-bus-name-not-found</summary>
[Category("AwsEvaluationSuite")]
public sealed class CustomBusRuleCallsWithoutEventBusNameNotFound
{
    /// <summary>
    /// DescribeRule, ListTargetsByRule, PutTargets and DisableRule on a custom-bus rule without
    /// EventBusName fail with ResourceNotFoundException naming the default bus and attach nothing;
    /// with EventBusName as the bus ARN they succeed and DisableRule takes effect.
    /// </summary>
    [Test]
    [DisplayName("events.custom-bus-rule-calls-without-event-bus-name-not-found")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ev = ctx.Events;
        var bus = FidelityContext.UniqueName("sim2real-behav-bus-");
        var busArn = (await ev.CreateEventBusAsync(new CreateEventBusRequest { Name = bus, Tags = [new Tag { Key = "sim2real", Value = "behavioral" }] })).EventBusArn;
        ctx.OnCleanup(() => ev.DeleteEventBusAsync(new DeleteEventBusRequest { Name = bus }));

        var rule = FidelityContext.UniqueName("sim2real-behav-rule-");
        await ev.PutRuleAsync(new PutRuleRequest { Name = rule, EventBusName = bus, EventPattern = """{"source": ["sim2real.scoped"]}""" });
        ctx.OnCleanup(() => ev.DeleteRuleAsync(new DeleteRuleRequest { Name = rule, EventBusName = bus }));

        var partition = busArn.Split(':')[1];
        var targetArn = $"arn:{partition}:sqs:{ctx.Region}:{ctx.Account}:" + FidelityContext.UniqueName("sim2real-behav-q-");
        var expected = $"Rule {rule} does not exist on EventBus default.";

        var calls = new (string Op, Func<Task> Fn)[]
        {
            ("DescribeRule", () => ev.DescribeRuleAsync(new DescribeRuleRequest { Name = rule })),
            ("ListTargetsByRule", () => ev.ListTargetsByRuleAsync(new ListTargetsByRuleRequest { Rule = rule })),
            ("PutTargets", () => ev.PutTargetsAsync(new PutTargetsRequest { Rule = rule, Targets = [new Target { Id = "t1", Arn = targetArn }] })),
            ("DisableRule", () => ev.DisableRuleAsync(new DisableRuleRequest { Name = rule })),
        };
        foreach (var (op, fn) in calls)
        {
            var (code, message) = await Error(fn);
            Ensure(code == "ResourceNotFoundException", $"{op} on a custom-bus rule without EventBusName gave '{code}'");
            Ensure(message == expected, $"{op} on a custom-bus rule without EventBusName said '{message}'");
        }

        var onBus = await ev.DescribeRuleAsync(new DescribeRuleRequest { Name = rule, EventBusName = busArn });
        Ensure(
            onBus.Name == rule && onBus.EventBusName == bus,
            "DescribeRule by bus ARN returned Name/EventBusName " +
            $"'{onBus.Name}'/'{onBus.EventBusName}'");
        Ensure(
            onBus.State?.Value == "ENABLED",
            $"the refused DisableRule changed the custom-bus rule to '{onBus.State?.Value}'");
        Ensure(
            ((await ev.ListTargetsByRuleAsync(new ListTargetsByRuleRequest { Rule = rule, EventBusName = busArn })).Targets ?? []).Count == 0,
            "the refused PutTargets attached a target to the custom-bus rule");

        var ok = await ev.PutTargetsAsync(new PutTargetsRequest { Rule = rule, EventBusName = busArn, Targets = [new Target { Id = "t1", Arn = targetArn }] });
        ctx.OnCleanup(() => ev.RemoveTargetsAsync(new RemoveTargetsRequest { Rule = rule, EventBusName = bus, Ids = ["t1"] }));
        Ensure(ok.FailedEntryCount == 0, $"PutTargets with the bus ARN was refused: {ok}");
        var listed = ((await ev.ListTargetsByRuleAsync(new ListTargetsByRuleRequest { Rule = rule, EventBusName = bus })).Targets ?? []).Select(t => t.Id).ToList();
        Ensure(listed.SequenceEqual(["t1"]), $"after PutTargets with the bus ARN the rule lists targets [{string.Join(", ", listed.Select(i => $"'{i}'"))}]");

        await ev.DisableRuleAsync(new DisableRuleRequest { Name = rule, EventBusName = busArn });
        var state = (await ev.DescribeRuleAsync(new DescribeRuleRequest { Name = rule, EventBusName = bus })).State?.Value;
        Ensure(state == "DISABLED", $"DisableRule with the bus ARN left the rule '{state}'");
    }

    /// <summary>The (Error.Code, Error.Message) <c>fn</c> raises. An unexpected success raises too.</summary>
    private static async Task<(string? Code, string? Message)> Error(Func<Task> fn)
    {
        var error = await FidelityContext.CaptureError(fn);
        if (error is null)
        {
            throw Fail("expected the call to be refused, but it succeeded");
        }

        return (error.Code, error.Message);
    }
}
