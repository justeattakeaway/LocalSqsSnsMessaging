// Translated from behavioral/events/check_list_event_buses_limit_above_the_documented_maximum_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit abd0811).
using Amazon.EventBridge.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.Events;

/// <summary>events.list-event-buses-limit-above-the-documented-maximum-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class ListEventBusesLimitAboveTheDocumentedMaximumRejected
{
    private const string Check = "list-event-buses-limit-above-the-documented-maximum-rejected";

    /// <summary>
    /// ListEventBuses refuses a Limit one past its documented maximum of 100, while the maximum
    /// itself still returns the bus a prefix search names.
    /// </summary>
    [Test]
    [DisplayName("events.list-event-buses-limit-above-the-documented-maximum-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var events = ctx.Events;
        var bus = FidelityContext.UniqueName("sim2real-behav-bus-");
        await events.CreateEventBusAsync(new CreateEventBusRequest { Name = bus, Tags = [new Tag { Key = "sim2real", Value = Check }] });
        ctx.OnCleanup(() => events.DeleteEventBusAsync(new DeleteEventBusRequest { Name = bus }));

        var overMaximum = 101; // a variable: the SDK's analyzer refuses the literal
        var (result, error) = await FidelityContext.Capture(() => events.ListEventBusesAsync(new ListEventBusesRequest { Limit = overMaximum }));
        if (error is null)
        {
            throw Fail($"expected the call to be refused, but it succeeded: {result}");
        }

        Ensure(
            (error.Code, error.Message) == (
                "ValidationException",
                "1 validation error detected: Value '101' at 'limit' failed to satisfy constraint: " +
                "Member must have value less than or equal to 100"),
            $"Limit=101 must be refused exactly, got '{error.Code}' '{error.Message}'");

        var found = (await events.ListEventBusesAsync(new ListEventBusesRequest { NamePrefix = bus, Limit = 100 })).EventBuses ?? [];
        var names = found.Select(b => b.Name).ToList();
        Ensure(names.SequenceEqual([bus]), $"the documented maximum Limit must still list the bus, got {Py.Repr(names)}");
    }
}
