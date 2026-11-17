// Translated from behavioral/events/check_list_partner_event_sources_disabled_account_denied.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.EventBridge.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Events;

/// <summary>events.list-partner-event-sources-disabled-account-denied</summary>
[Category("AwsEvaluationSuite")]
public sealed class ListPartnerEventSourcesDisabledAccountDenied
{
    private const string Check = "list-partner-event-sources-disabled-account-denied";

    /// <summary>
    /// ListPartnerEventSources from an account without partner access fails with
    /// ValidationException for a well-formed aws.partner/ prefix instead of returning a list,
    /// and leaves an ordinary event bus untouched and listable.
    /// </summary>
    [Test]
    [DisplayName("events.list-partner-event-sources-disabled-account-denied")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var events = ctx.Events;

        // A real, ordinary resource that the rejected call must have zero effect on.
        var busName = FidelityContext.UniqueName("sim2real-behav-bus-");
        await events.CreateEventBusAsync(new CreateEventBusRequest { Name = busName, Tags = [new Tag { Key = "sim2real-check", Value = Check }] });
        ctx.OnCleanup(() => IgnoreNotFound(() => events.DeleteEventBusAsync(new DeleteEventBusRequest { Name = busName })));
        var before = await BusNames(events, busName);
        Ensure(before.Contains(busName), $"expected {busName} in list_event_buses right after create, got {{{string.Join(", ", before)}}}");

        var prefix = $"aws.partner/{FidelityContext.UniqueName("sim2real-behav-partner-")}/";
        var (code, message) = await Error(() =>
            events.ListPartnerEventSourcesAsync(new ListPartnerEventSourcesRequest { NamePrefix = prefix }));
        Ensure(code == "ValidationException", $"expected ValidationException, got '{code}'");
        Ensure(message == NotAllowlisted(prefix), $"unexpected message '{message}'");

        // Effect absent / no collateral damage: the rejected partner-scoped call left the check's
        // own ordinary bus exactly as it was.
        var after = await BusNames(events, busName);
        Ensure(
            before.SetEquals(after),
            $"ListPartnerEventSources rejection had a side effect: before={{{string.Join(", ", before)}}} after={{{string.Join(", ", after)}}}");

        // Positive control: an ordinary (non-partner) listing operation still succeeds with the same
        // credentials and sees the real bus -- proving the rejection above is a targeted
        // partner-privilege check, not a broken client or blanket deny.
        Ensure(after.Contains(busName), $"expected {busName} still in list_event_buses, got {{{string.Join(", ", after)}}}");
    }

    private static async Task<HashSet<string>> BusNames(Amazon.EventBridge.AmazonEventBridgeClient events, string prefix) =>
        [.. ((await events.ListEventBusesAsync(new ListEventBusesRequest { NamePrefix = prefix })).EventBuses ?? []).Select(bus => bus.Name)];

    /// <summary>
    /// EventBridge's refusal for a caller outside a partner namespace; it names
    /// aws.partner/&lt;namespace&gt;, the first two segments of the source name or prefix.
    /// </summary>
    private static string NotAllowlisted(string name)
    {
        var ns = string.Join("/", name.Split('/').Take(2));
        return $"Caller is not whitelisted for the event source partner namespace {ns}";
    }

    /// <summary>(code, message) of a call that must fail; an unexpected success raises.</summary>
    private static async Task<(string? Code, string? Message)> Error(Func<Task> fn)
    {
        var error = await FidelityContext.CaptureError(fn);
        if (error is null)
        {
            throw Fail("real AWS rejects this call, but it SUCCEEDED");
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
