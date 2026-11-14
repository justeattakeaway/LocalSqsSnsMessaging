// Translated from behavioral/events/check_delete_event_bus_blocked_while_a_rule_remains_and_succeeds_once_removed.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.EventBridge;
using Amazon.EventBridge.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Events;

/// <summary>events.delete-event-bus-blocked-while-a-rule-remains-and-succeeds-once-removed</summary>
[Category("AwsEvaluationSuite")]
public sealed class DeleteEventBusBlockedWhileARuleRemainsAndSucceedsOnceRemoved
{
    private const string ExpectedBlockCode = "ValidationException";
    private const double BusGoneTimeoutS = 60.0;

    /// <summary>
    /// DeleteEventBus on a custom bus with a plain rule attached is a 400 ValidationException that
    /// leaves bus and rule intact; the identical call succeeds once the rule is removed.
    /// </summary>
    [Test]
    [DisplayName("events.delete-event-bus-blocked-while-a-rule-remains-and-succeeds-once-removed")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    [KnownFidelityGap(FidelityGap.EventBridgeEventBuses, "DeleteEventBus of a bus that still has a rule succeeds instead of failing with ValidationException 'EventBus can't be deleted since it has rules.'")]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var events = ctx.Events;

        // Positive control: DeleteEventBus works on an undecorated bus.
        var controlBus = FidelityContext.UniqueName("sim2real-behav-bus-");
        await events.CreateEventBusAsync(new CreateEventBusRequest { Name = controlBus, Tags = [new Tag { Key = "sim2real:managed", Value = "true" }] });
        ctx.OnCleanup(() => DeleteBusQuietly(events, controlBus));
        await events.DeleteEventBusAsync(new DeleteEventBusRequest { Name = controlBus });
        var controlGone = await PollUntil(
            () => IsNotFound(() => events.DescribeEventBusAsync(new DescribeEventBusRequest { Name = controlBus })),
            gone => gone,
            timeoutS: BusGoneTimeoutS);
        Ensure(
            controlGone,
            $"positive control failed: after DeleteEventBus({controlBus}) DescribeEventBus must " +
            "eventually return ResourceNotFoundException -- DeleteEventBus is not working in this " +
            "account/region, so the refusal asserted below would prove nothing");

        var bus = FidelityContext.UniqueName("sim2real-behav-bus-");
        await events.CreateEventBusAsync(new CreateEventBusRequest { Name = bus, Tags = [new Tag { Key = "sim2real:managed", Value = "true" }] });
        ctx.OnCleanup(() => DeleteBusQuietly(events, bus));

        var rule = FidelityContext.UniqueName("sim2real-behav-rule-");
        await events.PutRuleAsync(new PutRuleRequest
        {
            Name = rule,
            EventBusName = bus,
            EventPattern = """{"source": ["sim2real.deleteblock"]}""",
            Tags = [new Tag { Key = "sim2real:managed", Value = "true" }],
        });
        ctx.OnCleanup(() => DeleteRuleQuietly(events, rule, bus));

        var (code, message, status) = await Error(() => events.DeleteEventBusAsync(new DeleteEventBusRequest { Name = bus }));
        Ensure(
            code == ExpectedBlockCode,
            $"expected the rule dependency refusal to be '{ExpectedBlockCode}', got '{code}' with " +
            $"message '{message}'");
        Ensure(status == 400, $"the rule dependency refusal carried HTTP {status}, expected 400");
        const string expectedMessage = "EventBus can't be deleted since it has rules.";
        Ensure(
            message == expectedMessage,
            $"expected the refusal message \"{expectedMessage}\", got '{message}'");

        var stillThere = await events.DescribeEventBusAsync(new DescribeEventBusRequest { Name = bus });
        Ensure(
            stillThere.Name == bus,
            $"the refused delete must leave the bus describable, got {stillThere}");
        var ruleAfter = await events.DescribeRuleAsync(new DescribeRuleRequest { Name = rule, EventBusName = bus });
        Ensure(
            ruleAfter.Name == rule,
            $"the refused delete must leave the rule describable, got {ruleAfter}");
        var listed = ((await events.ListEventBusesAsync(new ListEventBusesRequest { NamePrefix = bus })).EventBuses ?? []).Select(b => b.Name).ToList();
        Ensure(listed.Contains(bus), $"the refused delete must leave '{bus}' in ListEventBuses, got {PyList(listed)}");

        await events.DeleteRuleAsync(new DeleteRuleRequest { Name = rule, EventBusName = bus, Force = true });
        var ruleGone = await PollUntil(
            () => IsNotFound(() => events.DescribeRuleAsync(new DescribeRuleRequest { Name = rule, EventBusName = bus })),
            gone => gone,
            timeoutS: BusGoneTimeoutS);
        Ensure(
            ruleGone,
            $"DescribeRule({rule}) must eventually return ResourceNotFoundException once the rule is " +
            "deleted");

        await events.DeleteEventBusAsync(new DeleteEventBusRequest { Name = bus });
        var finalGone = await PollUntil(
            () => IsNotFound(() => events.DescribeEventBusAsync(new DescribeEventBusRequest { Name = bus })),
            gone => gone,
            timeoutS: BusGoneTimeoutS);
        Ensure(
            finalGone,
            $"once its rule was removed, the identical DeleteEventBus({bus}) must succeed and leave " +
            "DescribeEventBus eventually returning ResourceNotFoundException");
        var finalListed = ((await events.ListEventBusesAsync(new ListEventBusesRequest { NamePrefix = bus })).EventBuses ?? []).Select(b => b.Name).ToList();
        Ensure(
            finalListed.Count == 0,
            $"the deleted bus must be absent from ListEventBuses(NamePrefix='{bus}'), got {PyList(finalListed)}");
    }

    /// <summary>(error code, message, HTTP status) for a call expected to fail; a success raises.</summary>
    private static async Task<(string? Code, string Message, int? Status)> Error<T>(Func<Task<T>> fn)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is null)
        {
            throw Fail($"expected the call to be refused, but it succeeded: {result}");
        }

        return (error.Code, error.Message ?? "", (int)error.StatusCode);
    }

    /// <summary>
    /// A poll probe: True once fn() raises ResourceNotFoundException, False while it still
    /// succeeds or raises anything else.
    /// </summary>
    private static async Task<bool> IsNotFound(Func<Task> fn) =>
        (await FidelityContext.CaptureError(fn))?.Code == "ResourceNotFoundException";

    private static async Task<T> PollUntil<T>(Func<Task<T>> probe, Func<T, bool> predicate, double timeoutS, double intervalS = 2.0)
    {
        var deadline = FidelityTime.StartNew();
        T? last = default;
        while (deadline.Elapsed < TimeSpan.FromSeconds(timeoutS))
        {
            last = await probe();
            if (predicate(last))
            {
                return last;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(intervalS));
        }

        throw Fail($"condition not met within {timeoutS}s. Last result: {last}");
    }

    private static async Task DeleteRuleQuietly(IAmazonEventBridge events, string name, string bus)
    {
        var error = await FidelityContext.CaptureError(() => events.DeleteRuleAsync(new DeleteRuleRequest { Name = name, EventBusName = bus, Force = true }));
        if (error is not null && error.Exception is not Amazon.EventBridge.Model.ResourceNotFoundException)
        {
            throw new FidelityAssertionException($"could not delete rule {name}: {error.Code}", error.Exception);
        }
    }

    private static async Task DeleteBusQuietly(IAmazonEventBridge events, string name)
    {
        var error = await FidelityContext.CaptureError(() => events.DeleteEventBusAsync(new DeleteEventBusRequest { Name = name }));
        if (error is not null && error.Exception is not Amazon.EventBridge.Model.ResourceNotFoundException)
        {
            throw new FidelityAssertionException($"could not delete event bus {name}: {error.Code}", error.Exception);
        }
    }

    private static string PyList(IEnumerable<string> items) => "[" + string.Join(", ", items.Select(i => $"'{i}'")) + "]";
}
