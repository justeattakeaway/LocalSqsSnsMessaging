// Translated from behavioral/events/check_activate_event_source_nonexistent_not_found.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.EventBridge.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.Events;

/// <summary>events.activate-event-source-nonexistent-not-found</summary>
[Category("AwsEvaluationSuite")]
public sealed class ActivateEventSourceNonexistentNotFound
{
    /// <summary>
    /// ActivateEventSource on a never-created partner source returns ResourceNotFoundException,
    /// stably across two names, and creates nothing; an ordinary bus create/describe is the control.
    /// </summary>
    [Test]
    [DisplayName("events.activate-event-source-nonexistent-not-found")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var events = ctx.Events;

        var busName = FidelityContext.UniqueName("sim2real-behav-bus-");
        await events.CreateEventBusAsync(new CreateEventBusRequest { Name = busName, Tags = [new Tag { Key = "sim2real", Value = "behavioral" }] });
        ctx.OnCleanup(() => IgnoreNotFound(() => events.DeleteEventBusAsync(new DeleteEventBusRequest { Name = busName })));
        var described = await events.DescribeEventBusAsync(new DescribeEventBusRequest { Name = busName });
        Ensure(
            described.Name == busName,
            $"positive control failed: expected to describe '{busName}', got '{described.Name}'");

        // Without APN partner registration CreatePartnerEventSource is refused; the source cannot exist.
        var bogus = PartnerSourceName();
        var err = await Error(() => events.ActivateEventSourceAsync(new ActivateEventSourceRequest { Name = bogus }));
        var expected = NotFound(bogus);
        Ensure(
            err == expected,
            $"expected {expected} activating a never-shared partner source, got {err}");

        // repeat with a distinct nonexistent name: the not-found result is stable
        var otherBogus = PartnerSourceName();
        var errOther = await Error(() => events.ActivateEventSourceAsync(new ActivateEventSourceRequest { Name = otherBogus }));
        var expectedOther = NotFound(otherBogus);
        Ensure(
            errOther == expectedOther,
            $"expected {expectedOther} activating a second never-shared partner source, " +
            $"got {errOther}");

        // effect absent: the rejected activate call did not conjure a describable source
        var describeErr = await Error(() => events.DescribeEventSourceAsync(new DescribeEventSourceRequest { Name = bogus }));
        (string?, string?) expectedDescribe = ("ResourceNotFoundException", $"Event source {bogus} does not exist.");
        Ensure(
            describeErr == expectedDescribe,
            $"the rejected activate call must not fabricate a describable source, got {describeErr}");
    }

    /// <summary>The refusal AWS returns for Activate/DeactivateEventSource on a source never shared here.</summary>
    private static (string?, string?) NotFound(string name) => (
        "ResourceNotFoundException",
        $"Event source {name} or event bus {name} does not exist.");

    /// <summary>
    /// Build a name matching ^aws\.partner(/[.\-_A-Za-z0-9]+){2,}$ -- the pattern EventBridge
    /// requires for partner event source names.
    /// </summary>
    private static string PartnerSourceName()
    {
        var suffix = FidelityContext.UniqueName("src-").Trim('-');
        return $"aws.partner/sim2real.test/{suffix}";
    }

    /// <summary>Call fn() and return its (code, message); an unexpected success raises.</summary>
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
