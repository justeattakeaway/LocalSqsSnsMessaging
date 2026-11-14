#pragma warning disable CS8600, CS8601, CS8602, CS8604 // Nullable warnings - internal POCOs use nullable properties but values are set at runtime

using System.Text.Json;
using System.Text.Json.Nodes;
using LocalSqsSnsMessaging.EventBridge.Model;

namespace LocalSqsSnsMessaging;

// PutEvents and TestEventPattern.
internal sealed partial class InternalEventBridgeClient
{
    public Task<PutEventsResponse> PutEventsAsync(PutEventsRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        AuthorizePutEvents(request.Entries);

        var entries = new List<PutEventsResultEntry>();
        foreach (var entry in request.Entries ?? [])
        {
            var eventId = Guid.NewGuid().ToString();
            var eventBus = ResolveBus(entry.EventBusName);
            var envelope = EventBridgeEnvelope.Build(entry, eventId, _bus);
            EventBridgeEventDelivery.Route(eventBus, envelope, _bus);
            entries.Add(new PutEventsResultEntry { EventId = eventId });
        }

        _bus.RecordOperation(AwsServiceName.EventBridge, "PutEvents");
        return Task.FromResult(new PutEventsResponse
        {
            FailedEntryCount = 0,
            Entries = entries
        }.SetCommonProperties());
    }

    public Task<TestEventPatternResponse> TestEventPatternAsync(TestEventPatternRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!EventBridgeEventPattern.IsValid(request.EventPattern, out var error))
        {
            throw new InternalInvalidEventPatternException($"Event pattern is not valid. Reason: {error}");
        }

        JsonNode? @event;
        try
        {
            @event = JsonNode.Parse(request.Event ?? "{}");
        }
        catch (JsonException ex)
        {
            throw new InternalInvalidEventPatternException($"Event is not valid JSON. Reason: {ex.Message}");
        }

        var result = EventBridgeEventPattern.Matches(request.EventPattern, @event);
        _bus.RecordOperation(AwsServiceName.EventBridge, "TestEventPattern");
        return Task.FromResult(new TestEventPatternResponse { Result = result }.SetCommonProperties());
    }
}
