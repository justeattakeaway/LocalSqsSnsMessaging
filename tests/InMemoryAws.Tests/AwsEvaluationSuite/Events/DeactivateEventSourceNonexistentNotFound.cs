// Translated from behavioral/events/check_deactivate_event_source_nonexistent_not_found.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.EventBridge.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.Events;

/// <summary>events.deactivate-event-source-nonexistent-not-found</summary>
[Category("AwsEvaluationSuite")]
public sealed class DeactivateEventSourceNonexistentNotFound
{
    /// <summary>
    /// DeactivateEventSource on a never-shared partner source name is ResourceNotFoundException.
    ///
    /// Repeated with a second name, and the source stays undescribable afterwards.
    /// </summary>
    [Test]
    [DisplayName("events.deactivate-event-source-nonexistent-not-found")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var events = ctx.Events;

        // Positive control: a non-partner account cannot create partner sources, so a working bus
        // call proves the not-found below is a targeted existence guard, not a blanket deny.
        var busName = FidelityContext.UniqueName("sim2real-behav-bus-");
        await events.CreateEventBusAsync(new CreateEventBusRequest { Name = busName });
        ctx.OnCleanup(() => events.DeleteEventBusAsync(new DeleteEventBusRequest { Name = busName }));
        var describedBus = await events.DescribeEventBusAsync(new DescribeEventBusRequest { Name = busName });
        Ensure(
            describedBus.Name == busName,
            $"positive control failed: expected event bus '{busName}', got '{describedBus.Name}'");

        var bogus = PartnerSourceName();
        var err = await Error(() => events.DeactivateEventSourceAsync(new DeactivateEventSourceRequest { Name = bogus }));
        var expected = NotFound(bogus);
        Ensure(
            err == expected,
            $"expected {expected} deactivating a never-shared partner source, got {err}");

        var otherBogus = PartnerSourceName();
        var errOther = await Error(() => events.DeactivateEventSourceAsync(new DeactivateEventSourceRequest { Name = otherBogus }));
        var expectedOther = NotFound(otherBogus);
        Ensure(
            errOther == expectedOther,
            $"expected {expectedOther} deactivating a second never-shared partner source, " +
            $"got {errOther}");

        var describeErr = await Error(() => events.DescribeEventSourceAsync(new DescribeEventSourceRequest { Name = bogus }));
        (string?, string?) expectedDescribe = ("ResourceNotFoundException", $"Event source {bogus} does not exist.");
        Ensure(
            describeErr == expectedDescribe,
            $"the rejected deactivate call must not fabricate a describable source, got {describeErr}");
    }

    /// <summary>The refusal AWS returns for Activate/DeactivateEventSource on a source never shared here.</summary>
    private static (string?, string?) NotFound(string name) => (
        "ResourceNotFoundException",
        $"Event source {name} or event bus {name} does not exist.");

    /// <summary>A name matching the pattern EventBridge requires for partner event source names.</summary>
    private static string PartnerSourceName()
    {
        var suffix = FidelityContext.UniqueName("src-").Trim('-');
        return $"aws.partner/sim2real.test/{suffix}";
    }

    /// <summary>Call fn(); return its (code, message). An unexpected success raises.</summary>
    private static async Task<(string? Code, string? Message)> Error(Func<Task> fn)
    {
        var error = await FidelityContext.CaptureError(fn);
        if (error is null)
        {
            throw Fail("expected a ClientError, but the call SUCCEEDED");
        }

        return (error.Code, error.Message);
    }
}
