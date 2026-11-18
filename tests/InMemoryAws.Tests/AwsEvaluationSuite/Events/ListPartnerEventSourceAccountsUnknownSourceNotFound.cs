// Translated from behavioral/events/check_list_partner_event_source_accounts_unknown_source_not_found.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.EventBridge.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.Events;

/// <summary>events.list-partner-event-source-accounts-unknown-source-not-found</summary>
[Category("AwsEvaluationSuite")]
public sealed class ListPartnerEventSourceAccountsUnknownSourceNotFound
{
    /// <summary>
    /// ListPartnerEventSourceAccounts for an unregistered partner namespace fails with
    /// ValidationException (namespace check precedes the not-found check), stably across names, leaving
    /// an ordinary bus intact.
    /// </summary>
    [Test]
    [DisplayName("events.list-partner-event-source-accounts-unknown-source-not-found")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var events = ctx.Events;

        // A real, ordinary resource that the rejected calls must have zero effect on.
        var busName = FidelityContext.UniqueName("sim2real-behav-bus-");
        await events.CreateEventBusAsync(new CreateEventBusRequest { Name = busName });
        ctx.OnCleanup(() => IgnoreNotFound(() => events.DeleteEventBusAsync(new DeleteEventBusRequest { Name = busName })));
        var before = await BusNames(events, busName);
        Ensure(before.Contains(busName), $"expected {busName} in {{{string.Join(", ", before)}}}");

        var bogus = PartnerSourceName();
        var (code, message) = await Error(() =>
            events.ListPartnerEventSourceAccountsAsync(new ListPartnerEventSourceAccountsRequest { EventSourceName = bogus }));
        Ensure(code == "ValidationException", $"expected ValidationException, got '{code}'");
        Ensure(message == NotAllowlisted(bogus), $"unexpected message '{message}'");

        // A second distinct name shows the namespace rejection is stable.
        var otherBogus = PartnerSourceName();
        (code, message) = await Error(() =>
            events.ListPartnerEventSourceAccountsAsync(new ListPartnerEventSourceAccountsRequest { EventSourceName = otherBogus }));
        Ensure(code == "ValidationException", $"expected ValidationException, got '{code}'");
        Ensure(message == NotAllowlisted(otherBogus), $"unexpected message '{message}'");

        // The rejected calls must leave the ordinary bus untouched.
        var after = await BusNames(events, busName);
        Ensure(
            before.SetEquals(after),
            $"expected no change, before={{{string.Join(", ", before)}}} after={{{string.Join(", ", after)}}}");

        // Positive control: an ordinary listing with the same credentials still sees the bus.
        Ensure(after.Contains(busName), $"expected {busName} in {{{string.Join(", ", after)}}}");
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

    /// <summary>
    /// Build a name matching ^aws\.partner(/[.\-_A-Za-z0-9]+){2,}$ -- the pattern EventBridge
    /// requires for partner event source names.
    /// </summary>
    private static string PartnerSourceName()
    {
        var suffix = FidelityContext.UniqueName("src-").Trim('-');
        return $"aws.partner/sim2real.test/{suffix}";
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
