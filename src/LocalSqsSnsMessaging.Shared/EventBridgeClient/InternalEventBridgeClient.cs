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

    /// <summary>The resources of the region this client serves: it sees and creates only this region's event buses.</summary>
    private readonly AwsRegionResources _resources;

    /// <param name="bus">The bus holding the event buses.</param>
    /// <param name="region">
    /// The region the request was made in; <see langword="null"/> for the bus's
    /// <see cref="InMemoryAwsBus.CurrentRegion"/>.
    /// </param>
    internal InternalEventBridgeClient(InMemoryAwsBus bus, string? region = null)
    {
        _bus = bus;
        _resources = bus.Region(region);
    }

    private EventBusResource ResolveBus(string? nameOrArn)
    {
        var name = ExtractBusName(nameOrArn);
        if (string.Equals(name, DefaultBusName, StringComparison.Ordinal))
        {
            // The default bus always exists and was never created, so it has no creation time.
            return _resources.EventBuses.GetOrAdd(DefaultBusName, n => new EventBusResource { Name = n, Arn = EventBusArn(n) });
        }

        if (_resources.EventBuses.TryGetValue(name, out var eventBus))
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

        throw RuleResource.NotFound(ruleName, eventBus.Name);
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
        $"arn:aws:events:{_resources.Region}:{_bus.CurrentAccountId}:event-bus/{name}";

    private string RuleArn(string busName, string ruleName) =>
        string.Equals(busName, DefaultBusName, StringComparison.Ordinal)
            ? $"arn:aws:events:{_resources.Region}:{_bus.CurrentAccountId}:rule/{ruleName}"
            : $"arn:aws:events:{_resources.Region}:{_bus.CurrentAccountId}:rule/{busName}/{ruleName}";
}
