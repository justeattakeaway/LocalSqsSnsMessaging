// Translated from behavioral/events/check_put_rule_nonexistent_bus_fails.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.EventBridge.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Events;

/// <summary>events.put-rule-nonexistent-bus-fails</summary>
[Category("AwsEvaluationSuite")]
public sealed class PutRuleNonexistentBusFails
{
    /// <summary>
    /// OVERLY-PERMISSIVE: PutRule referencing a nonexistent EventBusName must fail, must not
    /// silently land the rule on the default bus, and the identical call must succeed once that
    /// bus is created.
    /// </summary>
    [Test]
    [DisplayName("events.put-rule-nonexistent-bus-fails")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ev = ctx.Events;
        const string pattern = """{"source": ["sim2real.behav"]}""";

        // positive control: PutRule against a bus that exists works at all, so a later rejection
        // is attributable to the missing bus and not to PutRule being rejected wholesale.
        var controlBus = FidelityContext.UniqueName("sim2real-behav-ctrlbus-");
        await ev.CreateEventBusAsync(new CreateEventBusRequest { Name = controlBus });
        ctx.OnCleanup(() => ev.DeleteEventBusAsync(new DeleteEventBusRequest { Name = controlBus }));
        var controlRule = FidelityContext.UniqueName("sim2real-behav-ctrlrule-");
        await ev.PutRuleAsync(new PutRuleRequest { Name = controlRule, EventBusName = controlBus, EventPattern = pattern });
        ctx.OnCleanup(() => ev.DeleteRuleAsync(new DeleteRuleRequest { Name = controlRule, EventBusName = controlBus, Force = true }));

        var busName = FidelityContext.UniqueName("sim2real-behav-bus-");
        var rule = FidelityContext.UniqueName("sim2real-behav-rule-");

        var err = await ClientError(() => ev.PutRuleAsync(new PutRuleRequest { Name = rule, EventBusName = busName, EventPattern = pattern }));
        Ensure(err.Code == "ResourceNotFoundException", $"expected ResourceNotFoundException, got {err}");
        var expectedMessage = $"Event bus {busName} does not exist.";
        Ensure(err.Message == expectedMessage, $"expected message '{expectedMessage}', got '{err.Message}'");

        var defaultRules = (await ListRuleNames(ev, rule, null)).ToHashSet();
        Ensure(
            !defaultRules.Contains(rule),
            "PutRule rejected for a nonexistent bus must not silently create the rule on the default bus");

        // restore: create the bus that was missing, then the identical call must now succeed.
        await ev.CreateEventBusAsync(new CreateEventBusRequest { Name = busName });
        ctx.OnCleanup(() => ev.DeleteEventBusAsync(new DeleteEventBusRequest { Name = busName }));
        await ev.PutRuleAsync(new PutRuleRequest { Name = rule, EventBusName = busName, EventPattern = pattern });
        ctx.OnCleanup(() => ev.DeleteRuleAsync(new DeleteRuleRequest { Name = rule, EventBusName = busName, Force = true }));

        var restoredRules = (await ListRuleNames(ev, rule, busName)).ToHashSet();
        Ensure(restoredRules.Contains(rule), "identical PutRule call must succeed once its bus exists");
    }

    // Python indexes list_rules(...)["Rules"], which raises KeyError when the response omits it.
    private static async Task<IEnumerable<string>> ListRuleNames(Amazon.EventBridge.AmazonEventBridgeClient ev, string prefix, string? bus) =>
        ((await ev.ListRulesAsync(new ListRulesRequest { NamePrefix = prefix, EventBusName = bus })).Rules
            ?? throw new KeyNotFoundException("'Rules'"))
        .Select(r => r.Name);

    private static async Task<AwsError> ClientError<T>(Func<Task<T>> fn)
        where T : class
    {
        var (_, error) = await FidelityContext.Capture(fn);
        return error ?? throw Fail("expected a ClientError but the call succeeded");
    }
}
