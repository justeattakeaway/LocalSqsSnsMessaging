// Translated from behavioral/events/check_put_events_nonexistent_bus_silently_dropped.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.EventBridge.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Events;

/// <summary>events.put-events-nonexistent-bus-silently-dropped</summary>
[Category("AwsEvaluationSuite")]
public sealed class PutEventsNonexistentBusSilentlyDropped
{
    /// <summary>
    /// PutEvents to a non-existent EventBusName returns HTTP success and an
    /// EventId per entry rather than a resource-not-found failure; the event
    /// is simply not routable anywhere.
    /// </summary>
    [Test]
    [DisplayName("events.put-events-nonexistent-bus-silently-dropped")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    [KnownFidelityGap(FidelityGap.EventBridgePutEvents, "PutEvents to a nonexistent EventBusName throws ResourceNotFoundException instead of accepting the entry with an EventId")]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ev = ctx.Events;
        var bogusBus = FidelityContext.UniqueName("sim2real-behav-evb-no-such-bus-");
        var resp = await ev.PutEventsAsync(new PutEventsRequest
        {
            Entries =
            [
                new PutEventsRequestEntry
                {
                    Source = FidelityContext.UniqueName("sim2real.behav.evb.source."),
                    DetailType = "BehavioralCheck",
                    Detail = """{"x": 1}""",
                    EventBusName = bogusBus,
                },
            ],
        });
        Ensure(
            (resp.FailedEntryCount ?? 0) == 0,
            $"expected no failures for nonexistent-bus entry, got {resp.FailedEntryCount}");
        Ensure(
            (resp.Entries ?? throw new KeyNotFoundException("'Entries'"))[0].EventId is not null,
            $"expected EventId even though bus does not exist: {resp.Entries[0].ErrorCode} {resp.Entries[0].ErrorMessage}");

        // Independent read at the destination: PutEvents must not have implicitly
        // created the bus it just accepted an entry for. If it had, "not routable
        // anywhere" would be false — there would now be a bus to route to.
        var e = await FidelityContext.CaptureError(() => ev.DescribeEventBusAsync(new DescribeEventBusRequest { Name = bogusBus }));
        if (e is null)
        {
            throw Fail(
                $"describe_event_bus succeeded for {bogusBus} — PutEvents apparently " +
                $"created the bus it was told did not exist");
        }

        Ensure(
            e.Code == "ResourceNotFoundException",
            $"expected ResourceNotFoundException describing a bus that was never " +
            $"created, got {e.Code}: {e.Message}");
        var expectedMessage = $"Event bus {bogusBus} does not exist.";
        Ensure(e.Message == expectedMessage, $"expected message '{expectedMessage}', got '{e.Message}'");
    }
}
