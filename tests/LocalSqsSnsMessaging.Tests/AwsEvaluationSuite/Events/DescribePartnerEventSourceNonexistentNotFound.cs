// Translated from behavioral/events/check_describe_partner_event_source_nonexistent_not_found.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.EventBridge.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Events;

/// <summary>events.describe-partner-event-source-nonexistent-not-found</summary>
[Category("AwsEvaluationSuite")]
public sealed class DescribePartnerEventSourceNonexistentNotFound
{
    /// <summary>
    /// DescribePartnerEventSource on a nonexistent name in an unregistered partner namespace is a
    /// ValidationException naming the namespace, not ResourceNotFoundException: the namespace
    /// allowlist is checked before existence. The exact message rules out a malformed-name error.
    /// </summary>
    [Test]
    [DisplayName("events.describe-partner-event-source-nonexistent-not-found")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var events = ctx.Events;

        var bogus = PartnerSourceName();
        var (code, message) = await ErrorCodeAndMessage(() =>
            events.DescribePartnerEventSourceAsync(new DescribePartnerEventSourceRequest { Name = bogus }));
        Ensure((code, message) == ("ValidationException", NotAllowlisted(bogus)), $"('{code}', '{message}')");

        var otherBogus = PartnerSourceName();
        var (otherCode, otherMessage) = await ErrorCodeAndMessage(() =>
            events.DescribePartnerEventSourceAsync(new DescribePartnerEventSourceRequest { Name = otherBogus }));
        Ensure(
            (otherCode, otherMessage) == ("ValidationException", NotAllowlisted(otherBogus)),
            $"('{otherCode}', '{otherMessage}')");

        // Positive control: the same credentials can manage an ordinary bus.
        var busName = FidelityContext.UniqueName("sim2real-behav-bus-");
        await events.CreateEventBusAsync(new CreateEventBusRequest { Name = busName });
        ctx.OnCleanup(() => IgnoreNotFound(() => events.DeleteEventBusAsync(new DeleteEventBusRequest { Name = busName })));
        var described = await events.DescribeEventBusAsync(new DescribeEventBusRequest { Name = busName });
        Ensure(described.Name == busName, $"{{'Name': '{described.Name}', 'Arn': '{described.Arn}'}}");
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

    /// <summary>A name matching the pattern EventBridge requires for partner event source names.</summary>
    private static string PartnerSourceName()
    {
        var suffix = FidelityContext.UniqueName("src-").Trim('-');
        return $"aws.partner/sim2real.test/{suffix}";
    }

    private static async Task<(string? Code, string Message)> ErrorCodeAndMessage(Func<Task> fn)
    {
        var error = await FidelityContext.CaptureError(fn);
        if (error is null)
        {
            throw Fail("expected ClientError, call succeeded");
        }

        return (error.Code, error.Message ?? "");
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
