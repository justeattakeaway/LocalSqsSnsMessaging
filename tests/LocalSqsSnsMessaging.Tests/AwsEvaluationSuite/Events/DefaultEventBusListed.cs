// Translated from behavioral/events/check_default_event_bus_listed.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.EventBridge.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Events;

/// <summary>events.default-event-bus-listed</summary>
[Category("AwsEvaluationSuite")]
public sealed class DefaultEventBusListed
{
    /// <summary>
    /// ListEventBuses always includes the account's default event bus, and its entry
    /// is distinguished from a created bus: an Arn scoped to this account/region, and no
    /// CreationTime (the default bus was never explicitly created).
    /// </summary>
    [Test]
    [DisplayName("events.default-event-bus-listed")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ev = ctx.Events;
        var account = ctx.Account;
        var buses = new Dictionary<string, EventBus>(StringComparer.Ordinal);
        string? nextToken = null;
        while (true)
        {
            var resp = await ev.ListEventBusesAsync(new ListEventBusesRequest { NextToken = nextToken });
            foreach (var b in resp.EventBuses ?? [])
            {
                buses[b.Name] = b;
            }

            if (string.IsNullOrEmpty(resp.NextToken))
            {
                break;
            }

            nextToken = resp.NextToken;
        }

        Ensure(buses.ContainsKey("default"), $"default event bus must always be present, got [{string.Join(", ", buses.Keys.Order(StringComparer.Ordinal).Select(n => $"'{n}'"))}]");
        var defaultBus = buses["default"];
        var expectedArnSuffix = $":{ctx.Region}:{account}:event-bus/default";
        Ensure(
            (defaultBus.Arn ?? "").EndsWith(expectedArnSuffix, StringComparison.Ordinal),
            $"default bus Arn must be scoped to this account/region, got {defaultBus.Arn}");
        Ensure(
            defaultBus.CreationTime is null,
            "the default bus was never explicitly created and must carry no CreationTime, " +
            $"got {{'Name': '{defaultBus.Name}', 'Arn': '{defaultBus.Arn}', 'CreationTime': {defaultBus.CreationTime}}}");
    }
}
