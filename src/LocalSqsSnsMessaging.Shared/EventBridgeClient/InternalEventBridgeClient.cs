#pragma warning disable CS8600, CS8601, CS8602, CS8604 // Nullable warnings - internal POCOs use nullable properties but values are set at runtime

namespace LocalSqsSnsMessaging;

/// <summary>
/// In-memory implementation of the EventBridge operations used by the generated handler.
/// Supports events, rules, targets and event buses, and routes matched events to SQS targets.
/// </summary>
/// <remarks>
/// Split into partial files by area: event buses, rules, targets, events (PutEvents and
/// TestEventPattern), tags, permissions, and archives and replays. This file holds the shared
/// lookups and ARN helpers.
/// </remarks>
internal sealed partial class InternalEventBridgeClient
{
    private const string DefaultBusName = "default";

    private readonly InMemoryAwsBus _bus;

    internal InternalEventBridgeClient(InMemoryAwsBus bus)
    {
        _bus = bus;
    }

    private EventBusResource ResolveBus(string? nameOrArn)
    {
        var name = ExtractBusName(nameOrArn);
        if (string.Equals(name, DefaultBusName, StringComparison.Ordinal))
        {
            return _bus.EventBuses.GetOrAdd(DefaultBusName, n => new EventBusResource
            {
                Name = n,
                Arn = EventBusArn(n),
                CreationTime = _bus.TimeProvider.GetUtcNow(),
                LastModifiedTime = _bus.TimeProvider.GetUtcNow()
            });
        }

        if (_bus.EventBuses.TryGetValue(name, out var eventBus))
        {
            return eventBus;
        }

        throw new InternalResourceNotFoundException($"Event bus {name} does not exist.");
    }

    private RuleResource GetRule(string? busNameOrArn, string? ruleName)
    {
        var eventBus = ResolveBus(busNameOrArn);
        if (ruleName is not null && eventBus.Rules.TryGetValue(ruleName, out var rule))
        {
            return rule;
        }

        throw new InternalResourceNotFoundException($"Rule {ruleName} does not exist on EventBus {eventBus.Name}.");
    }

    private static string ExtractBusName(string? nameOrArn)
    {
        if (string.IsNullOrEmpty(nameOrArn))
        {
            return DefaultBusName;
        }

        var idx = nameOrArn.IndexOf("event-bus/", StringComparison.Ordinal);
        return idx >= 0 ? nameOrArn[(idx + "event-bus/".Length)..] : nameOrArn;
    }

    private string EventBusArn(string name) =>
        $"arn:aws:events:{_bus.CurrentRegion}:{_bus.CurrentAccountId}:event-bus/{name}";

    private string RuleArn(string busName, string ruleName) =>
        string.Equals(busName, DefaultBusName, StringComparison.Ordinal)
            ? $"arn:aws:events:{_bus.CurrentRegion}:{_bus.CurrentAccountId}:rule/{ruleName}"
            : $"arn:aws:events:{_bus.CurrentRegion}:{_bus.CurrentAccountId}:rule/{busName}/{ruleName}";
}
