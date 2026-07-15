#pragma warning disable CS8600, CS8601, CS8602, CS8604 // Nullable warnings - internal POCOs use nullable properties but values are set at runtime

using System.Text.Json;
using System.Text.Json.Nodes;
using LocalSqsSnsMessaging.EventBridge.Model;

namespace LocalSqsSnsMessaging;

/// <summary>
/// In-memory implementation of the EventBridge operations used by the generated handler.
/// Supports events, rules, targets and event buses, and routes matched events to SQS targets.
/// </summary>
internal sealed class InternalEventBridgeClient
{
    private const string DefaultBusName = "default";

    private readonly InMemoryAwsBus _bus;

    internal InternalEventBridgeClient(InMemoryAwsBus bus)
    {
        _bus = bus;
    }

    // ---- Events ----

    public Task<PutEventsResponse> PutEventsAsync(PutEventsRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var entries = new List<PutEventsResultEntry>();
        foreach (var entry in request.Entries ?? [])
        {
            var eventId = Guid.NewGuid().ToString();
            var eventBus = ResolveBus(entry.EventBusName);
            var envelope = EventBridgeEventDelivery.BuildEnvelope(entry, eventId, _bus);
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

    // ---- Rules ----

    public Task<PutRuleResponse> PutRuleAsync(PutRuleRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!string.IsNullOrEmpty(request.EventPattern) && !EventBridgeEventPattern.IsValid(request.EventPattern, out var error))
        {
            throw new InternalInvalidEventPatternException($"Event pattern is not valid. Reason: {error}");
        }

        var eventBus = ResolveBus(request.EventBusName);
        var arn = RuleArn(eventBus.Name, request.Name);

        eventBus.Rules.AddOrUpdate(request.Name,
            _ => new RuleResource
            {
                Name = request.Name,
                Arn = arn,
                EventBusName = eventBus.Name,
                EventPattern = request.EventPattern,
                ScheduleExpression = request.ScheduleExpression,
                State = request.State ?? "ENABLED",
                Description = request.Description,
                RoleArn = request.RoleArn
            },
            (_, existing) =>
            {
                existing.EventPattern = request.EventPattern;
                existing.ScheduleExpression = request.ScheduleExpression;
                existing.State = request.State ?? "ENABLED";
                existing.Description = request.Description;
                existing.RoleArn = request.RoleArn;
                return existing;
            });

        _bus.RecordOperation(AwsServiceName.EventBridge, "PutRule", arn);
        return Task.FromResult(new PutRuleResponse { RuleArn = arn }.SetCommonProperties());
    }

    public Task<DeleteRuleResponse> DeleteRuleAsync(DeleteRuleRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var eventBus = ResolveBus(request.EventBusName);
        eventBus.Rules.TryRemove(request.Name, out _);

        _bus.RecordOperation(AwsServiceName.EventBridge, "DeleteRule");
        return Task.FromResult(new DeleteRuleResponse().SetCommonProperties());
    }

    public Task<DescribeRuleResponse> DescribeRuleAsync(DescribeRuleRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var rule = GetRule(request.EventBusName, request.Name);

        _bus.RecordOperation(AwsServiceName.EventBridge, "DescribeRule", rule.Arn);
        return Task.FromResult(new DescribeRuleResponse
        {
            Name = rule.Name,
            Arn = rule.Arn,
            EventPattern = rule.EventPattern,
            ScheduleExpression = rule.ScheduleExpression,
            State = rule.State,
            Description = rule.Description,
            RoleArn = rule.RoleArn,
            EventBusName = rule.EventBusName
        }.SetCommonProperties());
    }

    public Task<EnableRuleResponse> EnableRuleAsync(EnableRuleRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        GetRule(request.EventBusName, request.Name).State = "ENABLED";
        _bus.RecordOperation(AwsServiceName.EventBridge, "EnableRule");
        return Task.FromResult(new EnableRuleResponse().SetCommonProperties());
    }

    public Task<DisableRuleResponse> DisableRuleAsync(DisableRuleRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        GetRule(request.EventBusName, request.Name).State = "DISABLED";
        _bus.RecordOperation(AwsServiceName.EventBridge, "DisableRule");
        return Task.FromResult(new DisableRuleResponse().SetCommonProperties());
    }

    public Task<ListRulesResponse> ListRulesAsync(ListRulesRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var eventBus = ResolveBus(request.EventBusName);
        var rules = eventBus.Rules.Values
            .Where(r => string.IsNullOrEmpty(request.NamePrefix) || r.Name.StartsWith(request.NamePrefix, StringComparison.Ordinal))
            .OrderBy(r => r.Name, StringComparer.Ordinal)
            .Select(r => new Rule
            {
                Name = r.Name,
                Arn = r.Arn,
                EventPattern = r.EventPattern,
                State = r.State,
                Description = r.Description,
                ScheduleExpression = r.ScheduleExpression,
                RoleArn = r.RoleArn,
                EventBusName = r.EventBusName
            })
            .ToList();

        _bus.RecordOperation(AwsServiceName.EventBridge, "ListRules");
        return Task.FromResult(new ListRulesResponse { Rules = rules }.SetCommonProperties());
    }

    // ---- Targets ----

    public Task<PutTargetsResponse> PutTargetsAsync(PutTargetsRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var rule = GetRule(request.EventBusName, request.Rule);
        lock (rule.Targets)
        {
            foreach (var target in request.Targets ?? [])
            {
                rule.Targets.RemoveAll(t => string.Equals(t.Id, target.Id, StringComparison.Ordinal));
                rule.Targets.Add(target);
            }
        }

        _bus.RecordOperation(AwsServiceName.EventBridge, "PutTargets", rule.Arn);
        return Task.FromResult(new PutTargetsResponse
        {
            FailedEntryCount = 0,
            FailedEntries = []
        }.SetCommonProperties());
    }

    public Task<RemoveTargetsResponse> RemoveTargetsAsync(RemoveTargetsRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var rule = GetRule(request.EventBusName, request.Rule);
        lock (rule.Targets)
        {
            foreach (var id in request.Ids ?? [])
            {
                rule.Targets.RemoveAll(t => string.Equals(t.Id, id, StringComparison.Ordinal));
            }
        }

        _bus.RecordOperation(AwsServiceName.EventBridge, "RemoveTargets", rule.Arn);
        return Task.FromResult(new RemoveTargetsResponse
        {
            FailedEntryCount = 0,
            FailedEntries = []
        }.SetCommonProperties());
    }

    public Task<ListTargetsByRuleResponse> ListTargetsByRuleAsync(ListTargetsByRuleRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var rule = GetRule(request.EventBusName, request.Rule);
        List<Target> targets;
        lock (rule.Targets)
        {
            targets = [.. rule.Targets];
        }

        _bus.RecordOperation(AwsServiceName.EventBridge, "ListTargetsByRule", rule.Arn);
        return Task.FromResult(new ListTargetsByRuleResponse { Targets = targets }.SetCommonProperties());
    }

    public Task<ListRuleNamesByTargetResponse> ListRuleNamesByTargetAsync(ListRuleNamesByTargetRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var eventBus = ResolveBus(request.EventBusName);
        var names = eventBus.Rules.Values
            .Where(r =>
            {
                lock (r.Targets)
                {
                    return r.Targets.Any(t => string.Equals(t.Arn, request.TargetArn, StringComparison.Ordinal));
                }
            })
            .Select(r => r.Name)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        _bus.RecordOperation(AwsServiceName.EventBridge, "ListRuleNamesByTarget");
        return Task.FromResult(new ListRuleNamesByTargetResponse { RuleNames = names }.SetCommonProperties());
    }

    // ---- Event buses ----

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

    // ---- Testing ----

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

    // ---- Helpers ----

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

        throw new InternalResourceNotFoundException($"Rule {ruleName} does not exist on event bus {eventBus.Name}.");
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
