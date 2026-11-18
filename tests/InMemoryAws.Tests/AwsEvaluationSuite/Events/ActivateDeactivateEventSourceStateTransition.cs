// Translated from behavioral/events/check_activate_deactivate_event_source_state_transition.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.EventBridge.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.Events;

/// <summary>events.activate-deactivate-event-source-state-transition</summary>
[Category("AwsEvaluationSuite")]
public sealed class ActivateDeactivateEventSourceStateTransition
{
    /// <summary>
    /// Activate/Deactivate/DescribeEventSource reject a partner source never shared to this account
    /// with ResourceNotFoundException and its exact message, and never fabricate a describable source.
    /// Only a real SaaS partner can share a source, so the PENDING/ACTIVE transition is unreachable.
    /// </summary>
    [Test]
    [DisplayName("events.activate-deactivate-event-source-state-transition")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var events = ctx.Events;

        var partnerSegment = FidelityContext.UniqueName("sim2real-behav-partner-");
        var sourceName = $"aws.partner/{partnerSegment}/testnamespace/testevent";

        (string?, string?) notDescribed = (
            "ResourceNotFoundException",
            $"Event source {sourceName} does not exist.");
        (string?, string?) notFound = (
            "ResourceNotFoundException",
            $"Event source {sourceName} or event bus {sourceName} does not exist.");

        var describeErr = await Error(() => events.DescribeEventSourceAsync(new DescribeEventSourceRequest { Name = sourceName }));
        Ensure(
            describeErr == notDescribed,
            $"expected {notDescribed} describing an unshared source, got {describeErr}");

        var activateErr = await Error(() => events.ActivateEventSourceAsync(new ActivateEventSourceRequest { Name = sourceName }));
        Ensure(
            activateErr == notFound,
            $"expected {notFound} activating an unshared source, got {activateErr}");

        var describeAfterActivateErr = await Error(() => events.DescribeEventSourceAsync(new DescribeEventSourceRequest { Name = sourceName }));
        Ensure(
            describeAfterActivateErr == notDescribed,
            $"activate must not have created a describable source, got {describeAfterActivateErr}");

        var deactivateErr = await Error(() => events.DeactivateEventSourceAsync(new DeactivateEventSourceRequest { Name = sourceName }));
        Ensure(
            deactivateErr == notFound,
            $"expected {notFound} deactivating an unshared source, got {deactivateErr}");

        var describeAfterDeactivateErr = await Error(() => events.DescribeEventSourceAsync(new DescribeEventSourceRequest { Name = sourceName }));
        Ensure(
            describeAfterDeactivateErr == notDescribed,
            $"deactivate must not have created a describable source, got {describeAfterDeactivateErr}");

        // Positive control: an ordinary bus works with the same credentials, so the errors above are
        // a targeted existence guard rather than a blanket deny.
        var busName = FidelityContext.UniqueName("sim2real-behav-bus-");
        await events.CreateEventBusAsync(new CreateEventBusRequest { Name = busName });
        ctx.OnCleanup(() => IgnoreNotFound(() => events.DeleteEventBusAsync(new DeleteEventBusRequest { Name = busName })));
        var described = await events.DescribeEventBusAsync(new DescribeEventBusRequest { Name = busName });
        Ensure(
            described.Name == busName,
            $"expected the positive-control event bus '{busName}' to describe back its own name, " +
            $"got '{described.Name}'");
    }

    /// <summary>
    /// Call fn() and return its (code, message); an unexpected success raises, so an accepted
    /// call is reported as such rather than as an error-code mismatch.
    /// </summary>
    private static async Task<(string? Code, string? Message)> Error(Func<Task> fn)
    {
        var error = await FidelityContext.CaptureError(fn);
        if (error is null)
        {
            throw Fail("expected a ClientError, but the call SUCCEEDED");
        }

        return (error.Code, error.Message);
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
