#pragma warning disable CS8600, CS8601, CS8602, CS8604 // Nullable warnings - internal POCOs use nullable properties but values are set at runtime

using LocalSqsSnsMessaging.EventBridge.Model;

namespace LocalSqsSnsMessaging;

// Event buses: CreateEventBus, DescribeEventBus, UpdateEventBus, DeleteEventBus and ListEventBuses.
internal sealed partial class InternalEventBridgeClient
{
    public Task<CreateEventBusResponse> CreateEventBusAsync(CreateEventBusRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var arn = EventBusArn(request.Name);
        var created = new EventBusResource
        {
            Name = request.Name,
            Arn = arn,
            Description = request.Description,
            CreationTime = _bus.TimeProvider.GetUtcNow(),
            LastModifiedTime = _bus.TimeProvider.GetUtcNow()
        };

        if (!_bus.EventBuses.TryAdd(request.Name, created))
        {
            throw new InternalResourceAlreadyExistsException($"Event bus {request.Name} already exists.");
        }

        _bus.RecordOperation(AwsServiceName.EventBridge, "CreateEventBus", arn);
        return Task.FromResult(new CreateEventBusResponse
        {
            EventBusArn = arn,
            Description = request.Description
        }.SetCommonProperties());
    }

    public Task<DeleteEventBusResponse> DeleteEventBusAsync(DeleteEventBusRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!string.Equals(request.Name, DefaultBusName, StringComparison.Ordinal))
        {
            _bus.EventBuses.TryRemove(request.Name, out _);
        }

        _bus.RecordOperation(AwsServiceName.EventBridge, "DeleteEventBus");
        return Task.FromResult(new DeleteEventBusResponse().SetCommonProperties());
    }

    public Task<DescribeEventBusResponse> DescribeEventBusAsync(DescribeEventBusRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var eventBus = ResolveBus(request.Name);

        _bus.RecordOperation(AwsServiceName.EventBridge, "DescribeEventBus", eventBus.Arn);
        return Task.FromResult(new DescribeEventBusResponse
        {
            Name = eventBus.Name,
            Arn = eventBus.Arn,
            Description = eventBus.Description,
            CreationTime = eventBus.CreationTime.UtcDateTime,
            LastModifiedTime = eventBus.LastModifiedTime.UtcDateTime
        }.SetCommonProperties());
    }

    public Task<ListEventBusesResponse> ListEventBusesAsync(ListEventBusesRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        // The default bus always exists.
        ResolveBus(DefaultBusName);

        var buses = _bus.EventBuses.Values
            .Where(b => string.IsNullOrEmpty(request.NamePrefix) || b.Name.StartsWith(request.NamePrefix, StringComparison.Ordinal))
            .OrderBy(b => b.Name, StringComparer.Ordinal)
            .Select(b => new EventBus
            {
                Name = b.Name,
                Arn = b.Arn,
                Description = b.Description,
                CreationTime = b.CreationTime.UtcDateTime,
                LastModifiedTime = b.LastModifiedTime.UtcDateTime
            })
            .ToList();

        _bus.RecordOperation(AwsServiceName.EventBridge, "ListEventBuses");
        return Task.FromResult(new ListEventBusesResponse { EventBuses = buses }.SetCommonProperties());
    }

    public Task<UpdateEventBusResponse> UpdateEventBusAsync(UpdateEventBusRequest request, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("EventBridge operation 'UpdateEventBus' is not supported.");
}
