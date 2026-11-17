// Translated from behavioral/events/check_delete_partner_event_source_disabled_operation_blocked.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.EventBridge.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Events;

/// <summary>events.delete-partner-event-source-disabled-operation-blocked</summary>
[Category("AwsEvaluationSuite")]
public sealed class DeletePartnerEventSourceDisabledOperationBlocked
{
    /// <summary>
    /// DeletePartnerEventSource from a non-partner account is a ValidationException naming the
    /// unregistered partner namespace, not a silent no-op, and has no side effect on event buses.
    /// </summary>
    [Test]
    [DisplayName("events.delete-partner-event-source-disabled-operation-blocked")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var events = ctx.Events;
        var account = ctx.Account;

        var partnerSegment = FidelityContext.UniqueName("sim2real-behav-partner-");
        var sourceName = $"aws.partner/{partnerSegment}/testnamespace/testevent";

        var baseline = await BusNames(events, sourceName);

        // The namespace check runs before authorization; OperationDisabledException is reachable
        // only for a registered partner namespace, which an ordinary account cannot construct.
        var err = await Error(() =>
            events.DeletePartnerEventSourceAsync(new DeletePartnerEventSourceRequest { Name = sourceName, Account = account }));
        var ns = sourceName[..sourceName.LastIndexOf('/', sourceName.LastIndexOf('/') - 1)];
        (string?, string?) expected = (
            "ValidationException",
            $"Caller is not whitelisted for the event source partner namespace {ns}");
        Ensure(err == expected, $"expected {expected}, got {err}");

        var after = await BusNames(events, sourceName);
        Ensure(
            baseline.SetEquals(after),
            $"expected no change, before={{{string.Join(", ", baseline)}}} after={{{string.Join(", ", after)}}}");

        // Positive control: the same credentials can manage an ordinary bus.
        var busName = FidelityContext.UniqueName("sim2real-behav-bus-");
        await events.CreateEventBusAsync(new CreateEventBusRequest { Name = busName });
        ctx.OnCleanup(() => IgnoreNotFound(() => events.DeleteEventBusAsync(new DeleteEventBusRequest { Name = busName })));
        var described = await events.DescribeEventBusAsync(new DescribeEventBusRequest { Name = busName });
        Ensure(described.Name == busName, "assert described[\"Name\"] == bus_name");
    }

    private static async Task<HashSet<string>> BusNames(Amazon.EventBridge.AmazonEventBridgeClient events, string prefix) =>
        [.. ((await events.ListEventBusesAsync(new ListEventBusesRequest { NamePrefix = prefix })).EventBuses ?? []).Select(bus => bus.Name)];

    /// <summary>Call fn(); return its (code, message). An unexpected SUCCESS raises.</summary>
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
