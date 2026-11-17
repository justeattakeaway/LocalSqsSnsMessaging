// Translated from behavioral/events/check_describe_event_source_nonexistent_not_found.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.EventBridge.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Events;

/// <summary>events.describe-event-source-nonexistent-not-found</summary>
[Category("AwsEvaluationSuite")]
public sealed class DescribeEventSourceNonexistentNotFound
{
    /// <summary>
    /// DescribeEventSource for a never-created partner event source fails with
    /// ResourceNotFoundException and ListEventSources does not list it; an ordinary event bus
    /// is a positive control showing the client and credentials are otherwise healthy.
    /// </summary>
    [Test]
    [DisplayName("events.describe-event-source-nonexistent-not-found")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ev = ctx.Events;

        var busName = FidelityContext.UniqueName("sim2real-behav-bus-");
        await ev.CreateEventBusAsync(new CreateEventBusRequest { Name = busName });
        ctx.OnCleanup(() => IgnoreNotFound(() => ev.DeleteEventBusAsync(new DeleteEventBusRequest { Name = busName })));
        var describedBus = await ev.DescribeEventBusAsync(new DescribeEventBusRequest { Name = busName });
        Ensure(describedBus.Name == busName, $"expected {busName}, got {describedBus.Name}");

        var bogus = "aws.partner/example.com/" + FidelityContext.UniqueName("src-");

        var err = await Error(() => ev.DescribeEventSourceAsync(new DescribeEventSourceRequest { Name = bogus }));
        Ensure(
            err == ("ResourceNotFoundException", $"Event source {bogus} does not exist."),
            $"expected ResourceNotFoundException, got {err}");

        var listed = (await ev.ListEventSourcesAsync(new ListEventSourcesRequest { NamePrefix = bogus })).EventSources ?? [];
        Ensure(listed.Count == 0, $"unknown partner event source must not be listed: [{string.Join(", ", listed.Select(s => s.Name))}]");
    }

    /// <summary>(code, message) of a call that must fail; a call that succeeds raises AssertionError.</summary>
    private static async Task<(string? Code, string? Message)> Error(Func<Task> fn)
    {
        var error = await FidelityContext.CaptureError(fn);
        if (error is null)
        {
            throw Fail("expected a ClientError, call succeeded");
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
