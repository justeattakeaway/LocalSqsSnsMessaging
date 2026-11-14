// Translated from behavioral/events/check_create_duplicate_event_bus_fails.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.EventBridge.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Events;

/// <summary>events.create-duplicate-event-bus-fails</summary>
[Category("AwsEvaluationSuite")]
public sealed class CreateDuplicateEventBusFails
{
    /// <summary>
    /// CreateEventBus with a Name that already belongs to an existing bus is rejected with
    /// ResourceAlreadyExistsException instead of silently succeeding or overwriting the original
    /// bus's configuration, and CreateEventBus still works for a genuinely fresh name.
    /// </summary>
    [Test]
    [DisplayName("events.create-duplicate-event-bus-fails")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ev = ctx.Events;
        var name = FidelityContext.UniqueName("sim2real-behav-bus-");
        await ev.CreateEventBusAsync(new CreateEventBusRequest { Name = name, Description = "original" });
        ctx.OnCleanup(() => ev.DeleteEventBusAsync(new DeleteEventBusRequest { Name = name }));

        var err = await Error(() => ev.CreateEventBusAsync(new CreateEventBusRequest { Name = name, Description = "duplicate-attempt" }));
        var expected = ("ResourceAlreadyExistsException", $"Event bus {name} already exists.");
        Ensure(err == expected, $"expected duplicate CreateEventBus to fail with {Repr(expected)}, got {Repr(err)}");

        // Effect absent: the original bus's Description was NOT overwritten by the rejected
        // duplicate-name create call.
        var described = await ev.DescribeEventBusAsync(new DescribeEventBusRequest { Name = name });
        Ensure(
            described.Description == "original",
            "duplicate CreateEventBus must not mutate the existing bus, got Description=" +
            $"'{described.Description}'");

        // Positive control: CreateEventBus with a genuinely different Name still succeeds --
        // proving CreateEventBus works in general, and the rejection above is specifically about
        // the name collision.
        var otherName = FidelityContext.UniqueName("sim2real-behav-bus2-");
        var other = await ev.CreateEventBusAsync(new CreateEventBusRequest { Name = otherName, Description = "other" });
        ctx.OnCleanup(() => ev.DeleteEventBusAsync(new DeleteEventBusRequest { Name = otherName }));
        Ensure(
            other.EventBusArn.Contains(otherName, StringComparison.Ordinal),
            $"expected control CreateEventBus to succeed with an ARN naming '{otherName}', " +
            $"got '{other.EventBusArn}'");
    }

    /// <summary>Call fn(); return its (code, message). An unexpected SUCCESS raises.</summary>
    private static async Task<(string? Code, string? Message)> Error<T>(Func<Task<T>> fn)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is null)
        {
            throw Fail($"expected a ClientError, but the call SUCCEEDED and returned {result}");
        }

        return (error.Code, error.Message);
    }

    private static string Repr((string? Code, string? Message) e) => $"('{e.Code}', '{e.Message}')";
}
