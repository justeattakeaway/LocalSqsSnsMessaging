// Translated from behavioral/events/check_list_event_buses_includes_default_and_custom.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.EventBridge.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Events;

/// <summary>events.list-event-buses-includes-default-and-custom</summary>
[Category("AwsEvaluationSuite")]
public sealed class ListEventBusesIncludesDefaultAndCustom
{
    /// <summary>
    /// ListEventBuses reflects both the always-present default bus and a
    /// newly created custom bus, and NamePrefix filtering is honored.
    /// </summary>
    [Test]
    [DisplayName("events.list-event-buses-includes-default-and-custom")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ev = ctx.Events;
        var name = FidelityContext.UniqueName("sim2real-behav-evb-bus-");
        await ev.CreateEventBusAsync(new CreateEventBusRequest { Name = name });
        ctx.OnCleanup(() => ev.DeleteEventBusAsync(new DeleteEventBusRequest { Name = name }));

        var filtered = (await ev.ListEventBusesAsync(new ListEventBusesRequest { NamePrefix = name })).EventBuses
            ?? throw new KeyNotFoundException("EventBuses");
        Ensure(
            filtered.Select(b => b.Name).ToHashSet().SetEquals([name]),
            $"NamePrefix filter broken: [{string.Join(", ", filtered.Select(b => $"{{'Name': '{b.Name}', 'Arn': '{b.Arn}'}}"))}]");

        var defaultOnly = (await ev.ListEventBusesAsync(new ListEventBusesRequest { NamePrefix = "default" })).EventBuses
            ?? throw new KeyNotFoundException("EventBuses");
        Ensure(
            defaultOnly.Any(b => b.Name == "default"),
            $"default bus must always be listed, got [{string.Join(", ", defaultOnly.Select(b => $"{{'Name': '{b.Name}', 'Arn': '{b.Arn}'}}"))}]");
    }
}
