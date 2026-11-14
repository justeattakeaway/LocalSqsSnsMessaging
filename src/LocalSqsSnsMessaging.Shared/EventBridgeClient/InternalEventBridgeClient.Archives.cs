#pragma warning disable CS8600, CS8601, CS8602, CS8604 // Nullable warnings - internal POCOs use nullable properties but values are set at runtime

using LocalSqsSnsMessaging.EventBridge.Model;

namespace LocalSqsSnsMessaging;

// Archives and replays: CreateArchive, DescribeArchive, UpdateArchive, DeleteArchive, ListArchives,
// StartReplay, DescribeReplay, CancelReplay and ListReplays. Also the guards on the rules EventBridge
// manages (an archive's rule), and the scheduling of rules with a ScheduleExpression.
internal sealed partial class InternalEventBridgeClient
{
    /// <summary>What a rule EventBridge creates for an archive reports as its ManagedBy.</summary>
    private const string ArchiveRuleManager = "prod.vhs.events.aws.internal";

    private const int MaxArchiveListLimit = 100;

    public Task<CreateArchiveResponse> CreateArchiveAsync(CreateArchiveRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var eventPattern = string.IsNullOrEmpty(request.EventPattern) ? null : request.EventPattern;
        EventBridgeEventPattern.ValidateArchivePattern(eventPattern);
        var eventBus = ResolveBus(request.EventSourceArn);

        var archive = new ArchiveResource
        {
            Name = request.ArchiveName,
            Arn = $"arn:aws:events:{_bus.CurrentRegion}:{_bus.CurrentAccountId}:archive/{request.ArchiveName}",
            EventSourceArn = request.EventSourceArn,
            EventBusName = eventBus.Name,
            Description = request.Description,
            EventPattern = eventPattern,
            RetentionDays = request.RetentionDays ?? 0,
            KmsKeyIdentifier = request.KmsKeyIdentifier,
            CreationTime = _bus.TimeProvider.GetUtcNow()
        };
        if (!_bus.EventBridgeArchives.TryAdd(archive.Name, archive))
        {
            throw new InternalResourceAlreadyExistsException($"Archive {archive.Name} already exists.");
        }

        var managedRule = new RuleResource
        {
            Name = archive.ManagedRuleName,
            Arn = RuleArn(eventBus.Name, archive.ManagedRuleName),
            EventBusName = eventBus.Name,
            EventPattern = archive.ManagedRulePattern(),
            ManagedBy = ArchiveRuleManager,
            Archive = archive
        };
        managedRule.PutTargets([new Target { Id = archive.ManagedRuleName, Arn = archive.Arn }]);
        eventBus.Rules[managedRule.Name] = managedRule;

        _bus.RecordOperation(AwsServiceName.EventBridge, "CreateArchive", archive.Arn);
        return Task.FromResult(new CreateArchiveResponse
        {
            ArchiveArn = archive.Arn,
            State = ArchiveResource.Enabled,
            CreationTime = archive.CreationTime.UtcDateTime
        }.SetCommonProperties());
    }

    public Task<DescribeArchiveResponse> DescribeArchiveAsync(DescribeArchiveRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var archive = GetArchive(request.ArchiveName);

        _bus.RecordOperation(AwsServiceName.EventBridge, "DescribeArchive", archive.Arn);
        return Task.FromResult(new DescribeArchiveResponse
        {
            ArchiveArn = archive.Arn,
            ArchiveName = archive.Name,
            EventSourceArn = archive.EventSourceArn,
            Description = archive.Description,
            EventPattern = archive.EventPattern,
            State = ArchiveResource.Enabled,
            KmsKeyIdentifier = archive.KmsKeyIdentifier,
            RetentionDays = archive.RetentionDays,
            SizeBytes = archive.SizeBytes,
            EventCount = archive.EventCount,
            CreationTime = archive.CreationTime.UtcDateTime
        }.SetCommonProperties());
    }

    public Task<UpdateArchiveResponse> UpdateArchiveAsync(UpdateArchiveRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var archive = GetArchive(request.ArchiveName);
        if (request.EventPattern is not null)
        {
            // An empty pattern clears the archive's pattern, so it archives every event.
            var eventPattern = request.EventPattern.Length == 0 ? null : request.EventPattern;
            EventBridgeEventPattern.ValidateArchivePattern(eventPattern);
            archive.EventPattern = eventPattern;
            if (_bus.EventBuses.TryGetValue(archive.EventBusName, out var eventBus)
                && eventBus.Rules.TryGetValue(archive.ManagedRuleName, out var managedRule))
            {
                managedRule.EventPattern = archive.ManagedRulePattern();
            }
        }
        if (request.Description is not null)
        {
            archive.Description = request.Description;
        }
        if (request.RetentionDays is not null)
        {
            archive.RetentionDays = request.RetentionDays.Value;
        }
        if (request.KmsKeyIdentifier is not null)
        {
            archive.KmsKeyIdentifier = request.KmsKeyIdentifier.Length == 0 ? null : request.KmsKeyIdentifier;
        }

        _bus.RecordOperation(AwsServiceName.EventBridge, "UpdateArchive", archive.Arn);
        return Task.FromResult(new UpdateArchiveResponse
        {
            ArchiveArn = archive.Arn,
            State = ArchiveResource.Enabled,
            CreationTime = archive.CreationTime.UtcDateTime
        }.SetCommonProperties());
    }

    public Task<DeleteArchiveResponse> DeleteArchiveAsync(DeleteArchiveRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.ArchiveName is null || !_bus.EventBridgeArchives.TryRemove(request.ArchiveName, out var archive))
        {
            throw ArchiveNotFound(request.ArchiveName);
        }
        if (_bus.EventBuses.TryGetValue(archive.EventBusName, out var eventBus))
        {
            eventBus.Rules.TryRemove(archive.ManagedRuleName, out _);
        }

        _bus.RecordOperation(AwsServiceName.EventBridge, "DeleteArchive", archive.Arn);
        return Task.FromResult(new DeleteArchiveResponse().SetCommonProperties());
    }

    public Task<ListArchivesResponse> ListArchivesAsync(ListArchivesRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        EnsureAtMostOneListFilter("ListArchives", request.NamePrefix, request.EventSourceArn, request.State);
        var stateMatches = string.IsNullOrEmpty(request.State) || string.Equals(request.State, ArchiveResource.Enabled, StringComparison.Ordinal);
        var archives = _bus.EventBridgeArchives.Values
            .Where(a => string.IsNullOrEmpty(request.NamePrefix) || a.Name.StartsWith(request.NamePrefix, StringComparison.Ordinal))
            .Where(a => string.IsNullOrEmpty(request.EventSourceArn) || string.Equals(a.EventSourceArn, request.EventSourceArn, StringComparison.Ordinal))
            .Where(_ => stateMatches)
            .OrderBy(a => a.Name, StringComparer.Ordinal);
        var (page, nextToken) = new PaginatedList<ArchiveResource>(archives)
            .GetPage(a => a.Name, ArchiveListLimit(request.Limit), request.NextToken);

        _bus.RecordOperation(AwsServiceName.EventBridge, "ListArchives");
        return Task.FromResult(new ListArchivesResponse
        {
            Archives = [.. page.Select(a => new Archive
            {
                ArchiveName = a.Name,
                EventSourceArn = a.EventSourceArn,
                State = ArchiveResource.Enabled,
                RetentionDays = a.RetentionDays,
                SizeBytes = a.SizeBytes,
                EventCount = a.EventCount,
                CreationTime = a.CreationTime.UtcDateTime
            })],
            NextToken = nextToken
        }.SetCommonProperties());
    }

    public Task<StartReplayResponse> StartReplayAsync(StartReplayRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var archiveName = ArnResourceName(request.EventSourceArn, "archive/");
        if (!_bus.EventBridgeArchives.TryGetValue(archiveName, out var archive))
        {
            throw ArchiveValidationError($"Parameter EventSourceArn is not valid. Reason: Archive {archiveName} does not exist.");
        }

        var eventStartTime = ReplayTimeToUtc(request.EventStartTime);
        var eventEndTime = ReplayTimeToUtc(request.EventEndTime);
        if (eventStartTime >= eventEndTime)
        {
            throw ArchiveValidationError("Parameter EventEndTime is not valid. Reason: EventStartTime must be before EventEndTime.");
        }

        var destinationArn = request.Destination?.Arn;
        var destinationBusName = ExtractBusName(destinationArn);
        if (!string.Equals(destinationBusName, archive.EventBusName, StringComparison.Ordinal))
        {
            throw ArchiveValidationError("Parameter Destination.Arn is not valid. Reason: Cross event bus replay is not permitted.");
        }

        var filterArns = request.Destination?.FilterArns?.Count > 0 ? request.Destination.FilterArns : null;
        if (filterArns?.Any(arn => !string.Equals(RuleArnBusName(arn), destinationBusName, StringComparison.Ordinal)) == true)
        {
            throw ArchiveValidationError("Parameter Destination.FilterArns is not valid. Reason: Rule must be on the destination event bus.");
        }

        var replay = new ReplayResource
        {
            Name = request.ReplayName,
            Arn = $"arn:aws:events:{_bus.CurrentRegion}:{_bus.CurrentAccountId}:replay/{request.ReplayName}",
            Description = request.Description,
            EventSourceArn = request.EventSourceArn,
            Archive = archive,
            DestinationArn = destinationArn,
            DestinationBusName = destinationBusName,
            FilterArns = filterArns,
            EventStartTime = eventStartTime,
            EventEndTime = eventEndTime,
            Bus = _bus
        };
        if (!_bus.EventBridgeReplays.TryAdd(replay.Name, replay))
        {
            throw new InternalResourceAlreadyExistsException($"Replay {replay.Name} already exists.");
        }
        replay.Start();

        _bus.RecordOperation(AwsServiceName.EventBridge, "StartReplay", replay.Arn);
        return Task.FromResult(new StartReplayResponse
        {
            ReplayArn = replay.Arn,
            State = replay.State,
            ReplayStartTime = replay.ReplayStartTime.UtcDateTime
        }.SetCommonProperties());
    }

    public Task<DescribeReplayResponse> DescribeReplayAsync(DescribeReplayRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var replay = GetReplay(request.ReplayName);

        _bus.RecordOperation(AwsServiceName.EventBridge, "DescribeReplay", replay.Arn);
        return Task.FromResult(new DescribeReplayResponse
        {
            ReplayName = replay.Name,
            ReplayArn = replay.Arn,
            Description = replay.Description,
            State = replay.State,
            EventSourceArn = replay.EventSourceArn,
            Destination = new ReplayDestination
            {
                Arn = replay.DestinationArn,
                FilterArns = replay.FilterArns is null ? null : [.. replay.FilterArns]
            },
            EventStartTime = replay.EventStartTime.UtcDateTime,
            EventEndTime = replay.EventEndTime.UtcDateTime,
            EventLastReplayedTime = replay.EventLastReplayedTime?.UtcDateTime,
            ReplayStartTime = replay.ReplayStartTime.UtcDateTime,
            ReplayEndTime = replay.ReplayEndTime?.UtcDateTime
        }.SetCommonProperties());
    }

    public Task<CancelReplayResponse> CancelReplayAsync(CancelReplayRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var replay = GetReplay(request.ReplayName);
        if (!replay.TryCancel())
        {
            throw new AwsServiceException($"Replay {replay.Name} is not in a valid state for this operation.")
            {
                ErrorCode = "IllegalStatusException"
            };
        }

        _bus.RecordOperation(AwsServiceName.EventBridge, "CancelReplay", replay.Arn);
        return Task.FromResult(new CancelReplayResponse
        {
            ReplayArn = replay.Arn,
            State = ReplayResource.Cancelling
        }.SetCommonProperties());
    }

    public Task<ListReplaysResponse> ListReplaysAsync(ListReplaysRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        EnsureAtMostOneListFilter("ListReplays", request.NamePrefix, request.EventSourceArn, request.State);
        var replays = _bus.EventBridgeReplays.Values
            .Where(r => string.IsNullOrEmpty(request.NamePrefix) || r.Name.StartsWith(request.NamePrefix, StringComparison.Ordinal))
            .Where(r => string.IsNullOrEmpty(request.EventSourceArn) || string.Equals(r.EventSourceArn, request.EventSourceArn, StringComparison.Ordinal))
            .Where(r => string.IsNullOrEmpty(request.State) || string.Equals(r.State, request.State, StringComparison.Ordinal))
            .OrderBy(r => r.Name, StringComparer.Ordinal);
        var (page, nextToken) = new PaginatedList<ReplayResource>(replays)
            .GetPage(r => r.Name, ArchiveListLimit(request.Limit), request.NextToken);

        _bus.RecordOperation(AwsServiceName.EventBridge, "ListReplays");
        return Task.FromResult(new ListReplaysResponse
        {
            Replays = [.. page.Select(r => new Replay
            {
                ReplayName = r.Name,
                EventSourceArn = r.EventSourceArn,
                State = r.State,
                EventStartTime = r.EventStartTime.UtcDateTime,
                EventEndTime = r.EventEndTime.UtcDateTime,
                EventLastReplayedTime = r.EventLastReplayedTime?.UtcDateTime,
                ReplayStartTime = r.ReplayStartTime.UtcDateTime,
                ReplayEndTime = r.ReplayEndTime?.UtcDateTime
            })],
            NextToken = nextToken
        }.SetCommonProperties());
    }

    /// <summary>Refuses a customer write to a rule EventBridge manages.</summary>
    private static void ThrowIfManagedRule(RuleResource rule)
    {
        if (rule.ManagedBy is not null)
        {
            throw ManagedRuleError($"{rule.Name} is a managed rule.");
        }
    }

    /// <summary>Refuses a customer write to a rule EventBridge manages unless the request sets Force (DeleteRule, RemoveTargets).</summary>
    private static void ThrowIfManagedRule(RuleResource rule, bool? force)
    {
        if (rule.ManagedBy is not null && force != true)
        {
            throw ManagedRuleError($"{rule.Name} is a managed rule. Set 'force' parameter to true to override.");
        }
    }

    /// <summary>
    /// Starts, restarts or stops the job firing a rule's ScheduleExpression after PutRule. Only the
    /// default bus runs schedules (EventBridge accepts them nowhere else), so deleting a custom bus
    /// never leaves a schedule running.
    /// </summary>
    private void ApplySchedule(RuleResource rule)
    {
        var expression = string.Equals(rule.EventBusName, DefaultBusName, StringComparison.Ordinal) ? rule.ScheduleExpression : null;
        if (string.Equals(rule.Schedule?.Expression, expression, StringComparison.Ordinal))
        {
            return;
        }

        rule.Schedule?.Dispose();
        rule.Schedule = expression is null ? null : ScheduledRuleJob.Start(rule, expression, _bus);
    }

    private ArchiveResource GetArchive(string? name) =>
        name is not null && _bus.EventBridgeArchives.TryGetValue(name, out var archive) ? archive : throw ArchiveNotFound(name);

    private ReplayResource GetReplay(string? name) =>
        name is not null && _bus.EventBridgeReplays.TryGetValue(name, out var replay)
            ? replay
            : throw new InternalResourceNotFoundException($"Replay {name} does not exist.");

    private static InternalResourceNotFoundException ArchiveNotFound(string? name) => new($"Archive {name} does not exist.");

    /// <summary>ListArchives and ListReplays take at most one of their three filters.</summary>
    private static void EnsureAtMostOneListFilter(string operation, string? namePrefix, string? eventSourceArn, string? state)
    {
        var filters = new[] { namePrefix, eventSourceArn, state }.Count(f => !string.IsNullOrEmpty(f));
        if (filters > 1)
        {
            throw ArchiveValidationError($"At most one filter is allowed for {operation}. Use either : State, EventSourceArn, or NamePrefix.");
        }
    }

    private static int ArchiveListLimit(int? limit) => limit is > 0 and <= MaxArchiveListLimit ? limit.Value : MaxArchiveListLimit;

    /// <summary>The bus a rule ARN names: <c>rule/bus/name</c>, or the default bus for <c>rule/name</c>.</summary>
    private static string RuleArnBusName(string ruleArn)
    {
        var path = ArnResourceName(ruleArn, "rule/");
        var slash = path.IndexOf('/', StringComparison.Ordinal);
        return slash >= 0 ? path[..slash] : DefaultBusName;
    }

    /// <summary>The part of an ARN after <paramref name="resourceType"/> (e.g. <c>archive/</c>), or the whole value if it has none.</summary>
    private static string ArnResourceName(string? arn, string resourceType)
    {
        arn ??= string.Empty;
        var index = arn.IndexOf(resourceType, StringComparison.Ordinal);
        return index >= 0 ? arn[(index + resourceType.Length)..] : arn;
    }

    private static DateTimeOffset ReplayTimeToUtc(DateTime? time) =>
        time is not { } value ? default
        : value.Kind == DateTimeKind.Unspecified ? new DateTimeOffset(DateTime.SpecifyKind(value, DateTimeKind.Utc))
        : new DateTimeOffset(value.ToUniversalTime());

    private static AwsServiceException ArchiveValidationError(string message) => new(message) { ErrorCode = "ValidationException" };

    private static AwsServiceException ManagedRuleError(string message) => new(message) { ErrorCode = "ManagedRuleException" };
}
