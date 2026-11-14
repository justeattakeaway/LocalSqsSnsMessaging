#pragma warning disable CS8600, CS8601, CS8602, CS8604 // Nullable warnings - internal POCOs use nullable properties but values are set at runtime

using System.Text.Json;
using System.Text.Json.Nodes;
using LocalSqsSnsMessaging.EventBridge.Model;

namespace LocalSqsSnsMessaging;

// PutEvents and TestEventPattern.
internal sealed partial class InternalEventBridgeClient
{
    /// <summary>
    /// Accepts each valid entry with a fresh EventId and routes it through its bus's rules; an
    /// invalid entry fails in place with an ErrorCode and no EventId. An entry for a bus that does
    /// not exist is accepted and dropped. A request with too many entries, or whose entries are too
    /// large together, is refused whole.
    /// </summary>
    public Task<PutEventsResponse> PutEventsAsync(PutEventsRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        AuthorizePutEvents(request.Entries);

        var entries = request.Entries ?? [];
        EventBridgePutEventsValidation.ValidateRequest(entries);

        var results = new List<PutEventsResultEntry>(entries.Count);
        var failed = 0;
        foreach (var entry in entries)
        {
            if (!EventBridgePutEventsValidation.TryValidateEntry(entry, out var detail, out var failure))
            {
                results.Add(failure);
                failed++;
                continue;
            }

            var eventId = Guid.NewGuid().ToString();
            var @event = EventBridgeEnvelope.Build(entry, detail, eventId, _bus, _resources.Region);
            if (FindPutEventsBus(entry.EventBusName) is { } eventBus)
            {
                EventBridgeEventDelivery.Route(eventBus, @event, _bus);
            }
            results.Add(new PutEventsResultEntry { EventId = eventId });
        }

        _bus.RecordOperation(AwsServiceName.EventBridge, "PutEvents");
        return Task.FromResult(new PutEventsResponse
        {
            FailedEntryCount = failed,
            Entries = results
        }.SetCommonProperties());
    }

    /// <summary>The bus PutEvents delivers an entry to, or <see langword="null"/> when there is no such bus.</summary>
    private EventBusResource? FindPutEventsBus(string? nameOrArn)
    {
        var name = ExtractBusName(nameOrArn);
        if (string.Equals(name, DefaultBusName, StringComparison.Ordinal))
        {
            return ResolveBus(DefaultBusName);
        }
        return _resources.EventBuses.TryGetValue(name, out var eventBus) ? eventBus : null;
    }

    public Task<TestEventPatternResponse> TestEventPatternAsync(TestEventPatternRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        EventBridgeEventPattern.ValidateTestPattern(request.EventPattern);
        var @event = EventBridgeTestEvent.Parse(request.Event);

        var result = EventBridgeEventPattern.Matches(request.EventPattern, @event);
        _bus.RecordOperation(AwsServiceName.EventBridge, "TestEventPattern");
        return Task.FromResult(new TestEventPatternResponse { Result = result }.SetCommonProperties());
    }
}
