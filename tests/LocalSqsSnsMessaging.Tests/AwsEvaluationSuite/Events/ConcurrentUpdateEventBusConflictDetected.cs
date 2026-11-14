// Translated from behavioral/events/check_concurrent_update_event_bus_conflict_detected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.EventBridge.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Events;

/// <summary>events.concurrent-update-event-bus-conflict-detected</summary>
[Category("AwsEvaluationSuite")]
public sealed class ConcurrentUpdateEventBusConflictDetected
{
    /// <summary>
    /// Many overlapping UpdateEventBus calls at one custom event bus leave at least one call
    /// succeeding (ConcurrentModificationException is a possible, not a guaranteed, outcome for the
    /// rest), and a subsequent solo update still works and persists (the bus is not left wedged).
    /// </summary>
    [Test]
    [DisplayName("events.concurrent-update-event-bus-conflict-detected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var events = ctx.Events;

        var busName = FidelityContext.UniqueName("sim2real-behav-bus-");
        await events.CreateEventBusAsync(new CreateEventBusRequest { Name = busName, Tags = [new Tag { Key = "sim2real:managed", Value = "true" }] });
        ctx.OnCleanup(() => IgnoreNotFound(() => events.DeleteEventBusAsync(new DeleteEventBusRequest { Name = busName })));

        const int racerCount = 12;
        const int maxBursts = 6;
        var successSeen = false;
        for (var burst = 0; burst < maxBursts; burst++)
        {
            var b = burst;
            var calls = Enumerable.Range(0, racerCount)
                .Select(i => (Func<Task>)(() => events.UpdateEventBusAsync(new UpdateEventBusRequest { Name = busName, Description = $"race-{b}-{i}" })))
                .ToList();
            var codes = await FireConcurrent(calls);
            if (codes.Any(code => code is null))
            {
                successSeen = true;
            }

            if (codes.Any(code => code == "ConcurrentModificationException"))
            {
                break;
            }
        }

        // ConcurrentModificationException is a possible UpdateEventBus error, not a guaranteed one, so a
        // backend that serializes the burst passes on the success it must still deliver.
        Ensure(successSeen, "expected at least one UpdateEventBus call in the race to succeed");

        // The bus itself must still be describable and mutable afterwards -- the race must not have
        // wedged it into a broken state.
        var described = await events.DescribeEventBusAsync(new DescribeEventBusRequest { Name = busName });
        Ensure(
            described.Name == busName,
            $"expected bus name '{busName}' after the race, got '{described.Name}'");

        // Positive control: a solo UpdateEventBus (no contention) still succeeds and persists.
        await events.UpdateEventBusAsync(new UpdateEventBusRequest { Name = busName, Description = "solo-update-control" });
        var describedAfter = await events.DescribeEventBusAsync(new DescribeEventBusRequest { Name = busName });
        Ensure(
            describedAfter.Description == "solo-update-control",
            "expected solo post-race update to persist, got " +
            $"'{describedAfter.Description}'");
    }

    /// <summary>
    /// Submit every callable before blocking on any result, then collect the error code (or
    /// None on success) for each -- maximizes true overlap on the wire.
    /// </summary>
    private static async Task<List<string?>> FireConcurrent(List<Func<Task>> fns)
    {
        var futures = fns.Select(fn => Task.Run(async () => (await FidelityContext.CaptureError(fn))?.Code)).ToList();
        return [.. await Task.WhenAll(futures)];
    }

    private static async Task IgnoreNotFound(Func<Task> fn)
    {
        var error = await FidelityContext.CaptureError(fn);
        if (error is not null && error.Code != "ResourceNotFoundException")
        {
            throw error.Exception;
        }
    }
}
