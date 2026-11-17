using System.Text.Json.Nodes;
using LocalSqsSnsMessaging.EventBridge.Model;

namespace LocalSqsSnsMessaging;

// Global endpoints: CreateEndpoint, DescribeEndpoint, UpdateEndpoint, DeleteEndpoint, ListEndpoints,
// and PutEvents through an endpoint (EndpointId). See EndpointResource for what is emulated.
internal sealed partial class InternalEventBridgeClient
{
    private const string EndpointNamePattern = @"[\.\-_A-Za-z0-9]+";
    private const string HealthCheckPattern = @"^arn:aws([a-z]|\-)*:route53:::healthcheck/[\-a-z0-9]+$";
    private const string RoutePattern = @"^[\-a-z0-9]+$";
    private const string NonPartnerEventBusArnPattern = @"^arn:aws[a-z-]*:events:[a-z]+-[a-z-]+-\d+:\d{12}:event-bus/[\w.-]+$";
    private const string IamRoleArnPattern = @"^arn:aws[a-z-]*:iam::\d{12}:role\/[\w+=,.@/-]+$";
    private const string EndpointIdPattern = @"^[A-Za-z0-9\-]+[\.][A-Za-z0-9\-]+$";

    public Task<CreateEndpointResponse> CreateEndpointAsync(CreateEndpointRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var violations = new List<string>();
        CheckPattern(violations, request.Name, "name", EndpointNamePattern, maxLength: 64);
        CheckDescription(violations, request.Description);
        CheckRoutingConfig(violations, request.RoutingConfig, required: true);
        CheckEventBuses(violations, request.EventBuses, required: true);
        CheckReplicationState(violations, request.ReplicationConfig);
        CheckRoleArn(violations, request.RoleArn);
        ThrowIfViolations(violations);

        var now = _bus.TimeProvider.GetUtcNow();
        var endpointId = EndpointResource.NewEndpointId();
        var endpoint = new EndpointResource
        {
            Name = request.Name!,
            Arn = $"arn:aws:events:{_resources.Region}:{_bus.CurrentAccountId}:endpoint/{request.Name}",
            EndpointId = endpointId,
            EndpointUrl = $"https://{endpointId}.endpoint.events.amazonaws.com",
            HomeRegion = _resources.Region,
            CreationTime = now,
            Settings = new EndpointSettings(
                NullIfEmpty(request.Description),
                request.RoutingConfig!.FailoverConfig!.Primary!.HealthCheck!,
                request.RoutingConfig.FailoverConfig.Secondary!.Route!,
                request.ReplicationConfig?.State ?? EndpointResource.ReplicationEnabled,
                [.. request.EventBuses!.Select(b => b.EventBusArn!)],
                NullIfEmpty(request.RoleArn),
                now)
        };
        if (!_resources.EventBridgeEndpoints.TryAdd(endpoint.Name, endpoint))
        {
            throw new InternalResourceAlreadyExistsException($"Endpoint {endpoint.Name} already exists.");
        }

        var settings = endpoint.Settings;
        _bus.RecordOperation(AwsServiceName.EventBridge, "CreateEndpoint", endpoint.Arn);
        return Task.FromResult(new CreateEndpointResponse
        {
            Name = endpoint.Name,
            Arn = endpoint.Arn,
            RoutingConfig = RoutingConfigOf(settings),
            ReplicationConfig = new ReplicationConfig { State = settings.ReplicationState },
            EventBuses = EventBusesOf(settings),
            RoleArn = settings.RoleArn,
            State = EndpointResource.Active
        }.SetCommonProperties());
    }

    public Task<DescribeEndpointResponse> DescribeEndpointAsync(DescribeEndpointRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var violations = new List<string>();
        CheckPattern(violations, request.Name, "name", EndpointNamePattern, maxLength: 64);
        ThrowIfViolations(violations);

        var endpoint = GetEndpoint(request.Name!, request.HomeRegion);
        var settings = endpoint.Settings;
        _bus.RecordOperation(AwsServiceName.EventBridge, "DescribeEndpoint", endpoint.Arn);
        return Task.FromResult(new DescribeEndpointResponse
        {
            Name = endpoint.Name,
            Description = settings.Description,
            Arn = endpoint.Arn,
            RoutingConfig = RoutingConfigOf(settings),
            ReplicationConfig = new ReplicationConfig { State = settings.ReplicationState },
            EventBuses = EventBusesOf(settings),
            RoleArn = settings.RoleArn,
            EndpointId = endpoint.EndpointId,
            EndpointUrl = endpoint.EndpointUrl,
            State = EndpointResource.Active,
            CreationTime = endpoint.CreationTime.UtcDateTime,
            LastModifiedTime = settings.LastModifiedTime.UtcDateTime
        }.SetCommonProperties());
    }

    public Task<UpdateEndpointResponse> UpdateEndpointAsync(UpdateEndpointRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var violations = new List<string>();
        CheckPattern(violations, request.Name, "name", EndpointNamePattern, maxLength: 64);
        CheckDescription(violations, request.Description);
        CheckRoutingConfig(violations, request.RoutingConfig, required: false);
        CheckEventBuses(violations, request.EventBuses, required: false);
        CheckReplicationState(violations, request.ReplicationConfig);
        CheckRoleArn(violations, request.RoleArn);
        ThrowIfViolations(violations);

        var endpoint = GetEndpoint(request.Name!, homeRegion: null);
        EndpointSettings settings;
        lock (endpoint.Gate)
        {
            var current = endpoint.Settings;
            var failover = request.RoutingConfig?.FailoverConfig;
            settings = current with
            {
                Description = request.Description is null ? current.Description : NullIfEmpty(request.Description),
                HealthCheck = failover?.Primary?.HealthCheck ?? current.HealthCheck,
                SecondaryRoute = failover?.Secondary?.Route ?? current.SecondaryRoute,
                ReplicationState = request.ReplicationConfig?.State ?? current.ReplicationState,
                EventBusArns = request.EventBuses is { Count: > 0 } buses ? [.. buses.Select(b => b.EventBusArn!)] : current.EventBusArns,
                RoleArn = request.RoleArn is null ? current.RoleArn : NullIfEmpty(request.RoleArn),
                LastModifiedTime = _bus.TimeProvider.GetUtcNow()
            };
            endpoint.Settings = settings;
        }

        _bus.RecordOperation(AwsServiceName.EventBridge, "UpdateEndpoint", endpoint.Arn);
        return Task.FromResult(new UpdateEndpointResponse
        {
            Name = endpoint.Name,
            Arn = endpoint.Arn,
            RoutingConfig = RoutingConfigOf(settings),
            ReplicationConfig = new ReplicationConfig { State = settings.ReplicationState },
            EventBuses = EventBusesOf(settings),
            RoleArn = settings.RoleArn,
            EndpointId = endpoint.EndpointId,
            EndpointUrl = endpoint.EndpointUrl,
            State = EndpointResource.Active
        }.SetCommonProperties());
    }

    /// <summary>Deletes an endpoint; deleting one that does not exist succeeds, as on AWS.</summary>
    public Task<DeleteEndpointResponse> DeleteEndpointAsync(DeleteEndpointRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var violations = new List<string>();
        CheckPattern(violations, request.Name, "name", EndpointNamePattern, maxLength: 64);
        ThrowIfViolations(violations);

        _resources.EventBridgeEndpoints.TryRemove(request.Name!, out _);
        _bus.RecordOperation(AwsServiceName.EventBridge, "DeleteEndpoint");
        return Task.FromResult(new DeleteEndpointResponse().SetCommonProperties());
    }

    public Task<ListEndpointsResponse> ListEndpointsAsync(ListEndpointsRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var violations = new List<string>();
        if (request.NamePrefix is not null)
        {
            CheckPattern(violations, request.NamePrefix, "namePrefix", EndpointNamePattern, maxLength: 64);
        }

        ThrowIfViolations(violations);

        var home = EndpointRegion(request.HomeRegion);
        var endpoints = home is null
            ? []
            : home.EventBridgeEndpoints.Values
                .Where(e => string.IsNullOrEmpty(request.NamePrefix) || e.Name.StartsWith(request.NamePrefix, StringComparison.Ordinal))
                .OrderBy(e => e.Name, StringComparer.Ordinal)
                .ToList();
        var page = EventBridgePage.Take(endpoints, request.MaxResults, request.NextToken, out var nextToken);

        _bus.RecordOperation(AwsServiceName.EventBridge, "ListEndpoints");
        return Task.FromResult(new ListEndpointsResponse
        {
            Endpoints = [.. page.Select(endpoint =>
            {
                var settings = endpoint.Settings;
                return new EventBridge.Model.Endpoint
                {
                    Name = endpoint.Name,
                    Description = settings.Description,
                    Arn = endpoint.Arn,
                    RoutingConfig = RoutingConfigOf(settings),
                    ReplicationConfig = new ReplicationConfig { State = settings.ReplicationState },
                    EventBuses = EventBusesOf(settings),
                    RoleArn = settings.RoleArn,
                    EndpointId = endpoint.EndpointId,
                    EndpointUrl = endpoint.EndpointUrl,
                    State = EndpointResource.Active,
                    CreationTime = endpoint.CreationTime.UtcDateTime,
                    LastModifiedTime = settings.LastModifiedTime.UtcDateTime
                };
            })],
            NextToken = nextToken
        }.SetCommonProperties());
    }

    /// <summary>
    /// PutEvents through a global endpoint: each entry must name one of the endpoint's two buses (an
    /// entry naming another fails in place), and goes to the endpoint's bus in its home region, which
    /// is always healthy; with replication enabled a copy also goes to its bus in the failover region.
    /// Both copies name the home region as their <c>region</c> and carry the endpoint's ARN in
    /// <c>resources</c>.
    /// </summary>
    private PutEventsResponse PutEventsThroughEndpoint(string endpointId, List<PutEventsRequestEntry> entries)
    {
        var violations = new List<string>();
        CheckPattern(violations, endpointId, "endpointId", EndpointIdPattern, maxLength: 50);
        ThrowIfViolations(violations);

        var endpoint = _bus.Regions
            .SelectMany(region => region.EventBridgeEndpoints.Values)
            .FirstOrDefault(e => string.Equals(e.EndpointId, endpointId, StringComparison.Ordinal))
            ?? throw new InternalResourceNotFoundException($"Endpoint {endpointId} does not exist.");
        var settings = endpoint.Settings;
        var primaryArn = settings.EventBusArns.FirstOrDefault(arn => string.Equals(Arn.RegionOf(arn), endpoint.HomeRegion, StringComparison.Ordinal));
        var secondaryArn = settings.EventBusArns.FirstOrDefault(arn => string.Equals(Arn.RegionOf(arn), settings.SecondaryRoute, StringComparison.Ordinal));

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

            var busName = string.IsNullOrEmpty(entry.EventBusName) ? DefaultBusName : entry.EventBusName!;
            var busArn = busName.StartsWith("arn:", StringComparison.Ordinal)
                ? busName
                : $"arn:aws:events:{endpoint.HomeRegion}:{_bus.CurrentAccountId}:event-bus/{busName}";
            if (!settings.EventBusArns.Contains(busArn, StringComparer.Ordinal))
            {
                results.Add(new PutEventsResultEntry
                {
                    ErrorCode = "InvalidArgument",
                    ErrorMessage = $"Parameter EventBusName is not valid. Reason: The endpoint is not associated with the event bus {busName}."
                });
                failed++;
                continue;
            }

            var eventId = Guid.NewGuid().ToString();
            var @event = EventBridgeEnvelope.Build(entry, detail, eventId, _bus, endpoint.HomeRegion);
            ((JsonArray)@event.Envelope["resources"]!).Add((JsonNode?)endpoint.Arn);
            if (primaryArn is not null && _bus.TryGetEventBusByArn(primaryArn, out var primary))
            {
                EventBridgeEventDelivery.Route(primary, @event, _bus);
            }

            if (string.Equals(settings.ReplicationState, EndpointResource.ReplicationEnabled, StringComparison.Ordinal)
                && secondaryArn is not null
                && _bus.TryGetEventBusByArn(secondaryArn, out var secondary))
            {
                var envelope = (JsonObject)@event.Envelope.DeepClone();
                envelope["id"] = Guid.NewGuid().ToString();
                EventBridgeEventDelivery.Route(secondary, new EventBridgeEvent { Envelope = envelope, IngestionTime = @event.IngestionTime, TraceHeader = @event.TraceHeader }, _bus);
            }

            results.Add(new PutEventsResultEntry { EventId = eventId });
        }

        _bus.RecordOperation(AwsServiceName.EventBridge, "PutEvents", endpoint.Arn);
        return new PutEventsResponse { FailedEntryCount = failed, Entries = results }.SetCommonProperties();
    }

    private EndpointResource GetEndpoint(string name, string? homeRegion)
    {
        var region = EndpointRegion(homeRegion);
        if (region is not null && region.EventBridgeEndpoints.TryGetValue(name, out var endpoint))
        {
            return endpoint;
        }

        throw new InternalResourceNotFoundException($"Endpoint {name} does not exist in region {homeRegion ?? _resources.Region}.");
    }

    /// <summary>The region whose endpoints a request with this HomeRegion reads; the request's own when it names none.</summary>
    private AwsRegionResources? EndpointRegion(string? homeRegion) =>
        string.IsNullOrEmpty(homeRegion) ? _resources : _bus.TryGetRegion(homeRegion, out var region) ? region : null;

    private static RoutingConfig RoutingConfigOf(EndpointSettings settings) => new()
    {
        FailoverConfig = new FailoverConfig
        {
            Primary = new Primary { HealthCheck = settings.HealthCheck },
            Secondary = new Secondary { Route = settings.SecondaryRoute }
        }
    };

    private static List<EndpointEventBus> EventBusesOf(EndpointSettings settings) =>
        [.. settings.EventBusArns.Select(arn => new EndpointEventBus { EventBusArn = arn })];

    private static void CheckDescription(List<string> violations, string? description)
    {
        if (description is { Length: > 512 })
        {
            violations.Add(Violation(description, "description", "Member must have length less than or equal to 512"));
        }
    }

    private static void CheckRoutingConfig(List<string> violations, RoutingConfig? routingConfig, bool required)
    {
        if (routingConfig is null)
        {
            if (required)
            {
                violations.Add(NullViolation("routingConfig"));
            }

            return;
        }

        var failover = routingConfig.FailoverConfig;
        if (failover is null)
        {
            violations.Add(NullViolation("routingConfig.failoverConfig"));
            return;
        }

        if (failover.Primary is null)
        {
            violations.Add(NullViolation("routingConfig.failoverConfig.primary"));
        }
        else
        {
            CheckPattern(violations, failover.Primary.HealthCheck, "routingConfig.failoverConfig.primary.healthCheck", HealthCheckPattern, maxLength: 1600);
        }

        if (failover.Secondary is null)
        {
            violations.Add(NullViolation("routingConfig.failoverConfig.secondary"));
        }
        else
        {
            CheckPattern(violations, failover.Secondary.Route, "routingConfig.failoverConfig.secondary.route", RoutePattern, maxLength: 20, minLength: 9);
        }
    }

    private static void CheckEventBuses(List<string> violations, List<EndpointEventBus>? eventBuses, bool required)
    {
        if (eventBuses is null || (eventBuses.Count == 0 && !required))
        {
            if (required)
            {
                violations.Add(NullViolation("eventBuses"));
            }

            return;
        }

        if (eventBuses.Count != 2)
        {
            var value = $"[{string.Join(", ", eventBuses.Select(b => $"EndpointEventBus(eventBusArn={b.EventBusArn})"))}]";
            violations.Add(Violation(value, "eventBuses", eventBuses.Count < 2
                ? "Member must have length greater than or equal to 2"
                : "Member must have length less than or equal to 2"));
        }

        for (var i = 0; i < eventBuses.Count; i++)
        {
            CheckPattern(violations, eventBuses[i].EventBusArn, $"eventBuses.{i + 1}.member.eventBusArn", NonPartnerEventBusArnPattern, maxLength: 512);
        }
    }

    private static void CheckReplicationState(List<string> violations, ReplicationConfig? replicationConfig)
    {
        if (replicationConfig?.State is { } state && state is not ("ENABLED" or "DISABLED"))
        {
            violations.Add(Violation(state, "replicationConfig.state", "Member must satisfy enum value set: [ENABLED, DISABLED]"));
        }
    }

    private static void CheckRoleArn(List<string> violations, string? roleArn)
    {
        if (roleArn is not null)
        {
            CheckPattern(violations, roleArn, "roleArn", IamRoleArnPattern, maxLength: 256);
        }
    }

    private static string NullViolation(string member) =>
        $"Value null at '{member}' failed to satisfy constraint: Member must not be null";
}
