// Translated from behavioral/events/check_partner_event_source_lifecycle_create_rejected_non_partner.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.EventBridge.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Events;

/// <summary>events.partner-event-source-lifecycle-create-rejected-non-partner</summary>
[Category("AwsEvaluationSuite")]
public sealed class PartnerEventSourceLifecycleCreateRejectedNonPartner
{
    /// <summary>
    /// Create, Describe and DeletePartnerEventSource from a non-partner account are all refused with
    /// the same partner-namespace ValidationException (never ResourceNotFound), while an ordinary
    /// event bus lifecycle with the same credentials still succeeds.
    /// </summary>
    [Test]
    [DisplayName("events.partner-event-source-lifecycle-create-rejected-non-partner")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var events = ctx.Events;
        var account = ctx.Account;

        var partnerSegment = FidelityContext.UniqueName("sim2real-behav-partner-");
        var sourceName = $"aws.partner/{partnerSegment}/testnamespace/testevent";

        // The namespace check runs before any privilege or existence check, so all three partner
        // operations fail with the same ValidationException.
        (string?, string?) refused = ("ValidationException", NotAllowlisted(sourceName));
        var createErr = await Error(() =>
            events.CreatePartnerEventSourceAsync(new CreatePartnerEventSourceRequest { Name = sourceName, Account = account }));
        Ensure(createErr == refused, $"expected {refused}, got {createErr}");

        var describeErr = await Error(() =>
            events.DescribePartnerEventSourceAsync(new DescribePartnerEventSourceRequest { Name = sourceName }));
        Ensure(
            describeErr == refused,
            $"partner event source must not have been created, expected {refused}, " +
            $"got {describeErr}");

        var deleteErr = await Error(() =>
            events.DeletePartnerEventSourceAsync(new DeletePartnerEventSourceRequest { Name = sourceName, Account = account }));
        Ensure(deleteErr == refused, $"expected {refused}, got {deleteErr}");

        // Positive control: the refusal above is targeted, not a blanket deny for these credentials.
        var busName = FidelityContext.UniqueName("sim2real-behav-bus-");
        await events.CreateEventBusAsync(new CreateEventBusRequest { Name = busName });
        ctx.OnCleanup(() => IgnoreNotFound(() => events.DeleteEventBusAsync(new DeleteEventBusRequest { Name = busName })));
        var described = await events.DescribeEventBusAsync(new DescribeEventBusRequest { Name = busName });
        Ensure(described.Name == busName, "assert described[\"Name\"] == bus_name");
    }

    /// <summary>
    /// EventBridge's refusal for a caller outside a partner namespace; it names
    /// aws.partner/&lt;namespace&gt;, the first two segments of the source name or prefix.
    /// </summary>
    private static string NotAllowlisted(string name)
    {
        var ns = string.Join("/", name.Split('/').Take(2));
        return $"Caller is not whitelisted for the event source partner namespace {ns}";
    }

    /// <summary>(code, message) of a call that must fail; a call that succeeds raises AssertionError.</summary>
    private static async Task<(string? Code, string? Message)> Error(Func<Task> fn)
    {
        var error = await FidelityContext.CaptureError(fn);
        if (error is null)
        {
            throw Fail("expected a ClientError, but the call succeeded");
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
