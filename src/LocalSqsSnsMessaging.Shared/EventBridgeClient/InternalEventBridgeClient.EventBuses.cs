#pragma warning disable CS8600, CS8601, CS8602, CS8604 // Nullable warnings - internal POCOs use nullable properties but values are set at runtime

using System.Text;
using System.Text.RegularExpressions;
using LocalSqsSnsMessaging.EventBridge.Model;

namespace LocalSqsSnsMessaging;

// Event buses: CreateEventBus, DescribeEventBus, UpdateEventBus, DeleteEventBus and ListEventBuses.
internal sealed partial class InternalEventBridgeClient
{
    private const int MaxEventBusNameLength = 256;
    private const int MaxEventBusDescriptionLength = 512;
    private const int MaxListEventBusesLimit = 100;

    /// <summary>The names CreateEventBus accepts (the API model's <c>EventBusName</c> pattern).</summary>
    private const string EventBusNamePattern = @"[/\.\-_A-Za-z0-9]+";

    private static readonly Regex EventBusNameRegex = new($"^{EventBusNamePattern}$", RegexOptions.CultureInvariant);

    /// <summary>Marks a ListEventBuses <c>NextToken</c> as one this library issued.</summary>
    private const string ListEventBusesTokenPrefix = "event-bus:";

    public Task<CreateEventBusResponse> CreateEventBusAsync(CreateEventBusRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var name = request.Name ?? "";
        var violations = new List<string>();
        if (name.Length > MaxEventBusNameLength)
        {
            violations.Add(Violation(name, "name", $"Member must have length less than or equal to {MaxEventBusNameLength}"));
        }
        if (!EventBusNameRegex.IsMatch(name))
        {
            violations.Add(Violation(name, "name", $"Member must satisfy regular expression pattern: {EventBusNamePattern}"));
        }
        AddDescriptionViolation(request.Description, violations);
        ThrowIfViolations(violations);

        EnsureNotAwsNamespace(name);
        if (name.Contains('/', StringComparison.Ordinal))
        {
            // Only partner event buses, which are created from an event source, have a '/' in their name.
            throw new InternalValidationException("Event bus name must not contain '/'.");
        }
        ValidateDeadLetterConfig(request.DeadLetterConfig);
        ValidateLogConfig(request.LogConfig);
        var tags = EventBridgeTagSet.From(request.Tags);

        var arn = EventBusArn(name);
        var now = _bus.TimeProvider.GetUtcNow();
        var created = new EventBusResource
        {
            Name = name,
            Arn = arn,
            Description = NullIfEmpty(request.Description),
            KmsKeyIdentifier = NullIfEmpty(request.KmsKeyIdentifier),
            DeadLetterConfig = request.DeadLetterConfig is { Arn: { Length: > 0 } } ? request.DeadLetterConfig : null,
            LogConfig = request.LogConfig,
            CreationTime = now,
            LastModifiedTime = now,
            Tags = tags
        };

        if (string.Equals(name, DefaultBusName, StringComparison.Ordinal) || !_bus.EventBuses.TryAdd(name, created))
        {
            throw new InternalResourceAlreadyExistsException($"Event bus {name} already exists.");
        }

        _bus.RecordOperation(AwsServiceName.EventBridge, "CreateEventBus", arn);
        return Task.FromResult(new CreateEventBusResponse
        {
            EventBusArn = arn,
            Description = created.Description,
            KmsKeyIdentifier = created.KmsKeyIdentifier,
            DeadLetterConfig = created.DeadLetterConfig,
            LogConfig = created.LogConfig
        }.SetCommonProperties());
    }

    public Task<DeleteEventBusResponse> DeleteEventBusAsync(DeleteEventBusRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var name = ExtractBusName(request.Name);
        if (string.Equals(name, DefaultBusName, StringComparison.Ordinal))
        {
            throw new InternalValidationException($"Cannot delete event bus {DefaultBusName}.");
        }

        // Deleting a bus that does not exist succeeds, as it does in EventBridge.
        if (_bus.EventBuses.TryGetValue(name, out var eventBus))
        {
            if (!eventBus.Rules.IsEmpty)
            {
                throw new InternalValidationException("EventBus can't be deleted since it has rules.");
            }

            _bus.EventBuses.TryRemove(name, out _);
        }

        _bus.RecordOperation(AwsServiceName.EventBridge, "DeleteEventBus");
        return Task.FromResult(new DeleteEventBusResponse().SetCommonProperties());
    }

    public Task<DescribeEventBusResponse> DescribeEventBusAsync(DescribeEventBusRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        EnsureNotAwsNamespace(ExtractBusName(request.Name));
        var eventBus = ResolveBus(request.Name);

        DescribeEventBusResponse response;
        lock (eventBus)
        {
            response = new DescribeEventBusResponse
            {
                Name = eventBus.Name,
                Arn = eventBus.Arn,
                Description = eventBus.Description,
                KmsKeyIdentifier = eventBus.KmsKeyIdentifier,
                DeadLetterConfig = eventBus.DeadLetterConfig,
                Policy = eventBus.Policy,
                LogConfig = eventBus.LogConfig,
                CreationTime = eventBus.CreationTime?.UtcDateTime,
                LastModifiedTime = eventBus.LastModifiedTime?.UtcDateTime
            };
        }

        _bus.RecordOperation(AwsServiceName.EventBridge, "DescribeEventBus", eventBus.Arn);
        return Task.FromResult(response.SetCommonProperties());
    }

    public Task<ListEventBusesResponse> ListEventBusesAsync(ListEventBusesRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var limit = request.Limit ?? MaxListEventBusesLimit;
        if (limit is < 1 or > MaxListEventBusesLimit)
        {
            var constraint = limit < 1 ? "greater than or equal to 1" : $"less than or equal to {MaxListEventBusesLimit}";
            ThrowIfViolations([Violation(limit.ToString(System.Globalization.CultureInfo.InvariantCulture), "limit", $"Member must have value {constraint}")]);
        }

        var startAfterOrAt = DecodeListEventBusesToken(request.NextToken);

        // The default bus always exists.
        ResolveBus(DefaultBusName);

        var matching = _bus.EventBuses.Values
            .Where(b => string.IsNullOrEmpty(request.NamePrefix) || b.Name.StartsWith(request.NamePrefix, StringComparison.Ordinal))
            .Where(b => startAfterOrAt is null || string.CompareOrdinal(b.Name, startAfterOrAt) >= 0)
            .OrderBy(b => b.Name, StringComparer.Ordinal)
            .Take(limit + 1)
            .ToList();

        var buses = matching.Take(limit).Select(b =>
        {
            lock (b)
            {
                return new EventBus
                {
                    Name = b.Name,
                    Arn = b.Arn,
                    Description = b.Description,
                    Policy = b.Policy,
                    CreationTime = b.CreationTime?.UtcDateTime,
                    LastModifiedTime = b.LastModifiedTime?.UtcDateTime
                };
            }
        }).ToList();

        _bus.RecordOperation(AwsServiceName.EventBridge, "ListEventBuses");
        return Task.FromResult(new ListEventBusesResponse
        {
            EventBuses = buses,
            NextToken = matching.Count > limit ? EncodeListEventBusesToken(matching[limit].Name) : null
        }.SetCommonProperties());
    }

    public Task<UpdateEventBusResponse> UpdateEventBusAsync(UpdateEventBusRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var violations = new List<string>();
        AddDescriptionViolation(request.Description, violations);
        ThrowIfViolations(violations);
        ValidateDeadLetterConfig(request.DeadLetterConfig);
        ValidateLogConfig(request.LogConfig);

        var eventBus = ResolveBus(request.Name);

        // A partial update: only the settings the request names change.
        UpdateEventBusResponse response;
        lock (eventBus)
        {
            if (request.Description is not null)
            {
                eventBus.Description = NullIfEmpty(request.Description);
            }
            if (request.KmsKeyIdentifier is not null)
            {
                eventBus.KmsKeyIdentifier = NullIfEmpty(request.KmsKeyIdentifier);
            }
            if (request.DeadLetterConfig is not null)
            {
                eventBus.DeadLetterConfig = string.IsNullOrEmpty(request.DeadLetterConfig.Arn) ? null : request.DeadLetterConfig;
            }
            if (request.LogConfig is not null)
            {
                eventBus.LogConfig = request.LogConfig;
            }
            if (eventBus.CreationTime is not null)
            {
                eventBus.LastModifiedTime = _bus.TimeProvider.GetUtcNow();
            }

            response = new UpdateEventBusResponse
            {
                Arn = eventBus.Arn,
                Name = eventBus.Name,
                Description = eventBus.Description,
                KmsKeyIdentifier = eventBus.KmsKeyIdentifier,
                DeadLetterConfig = eventBus.DeadLetterConfig,
                LogConfig = eventBus.LogConfig
            };
        }

        _bus.RecordOperation(AwsServiceName.EventBridge, "UpdateEventBus", eventBus.Arn);
        return Task.FromResult(response.SetCommonProperties());
    }

    /// <summary>Refuses a name in the <c>aws.</c> namespace, where no custom bus can exist.</summary>
    private static void EnsureNotAwsNamespace(string name)
    {
        if (name.StartsWith("aws.", StringComparison.Ordinal))
        {
            throw new InternalValidationException("EventBus name starting with 'aws.' is not valid.");
        }
    }

    private static void AddDescriptionViolation(string? description, List<string> violations)
    {
        if (description is { Length: > MaxEventBusDescriptionLength })
        {
            violations.Add(Violation(description, "description", $"Member must have length less than or equal to {MaxEventBusDescriptionLength}"));
        }
    }

    /// <summary>
    /// A bus's dead-letter queue must be named by an SQS ARN; the queue itself need not exist.
    /// A config without an ARN removes the dead-letter queue.
    /// </summary>
    private static void ValidateDeadLetterConfig(DeadLetterConfig? config)
    {
        if (config?.Arn is not { Length: > 0 } arn)
        {
            return;
        }

        var parts = arn.Split(':');
        if (parts.Length < 6 || !string.Equals(parts[0], "arn", StringComparison.Ordinal) || parts[2].Length == 0)
        {
            throw new InternalValidationException($"Parameter {arn} is not valid. Reason: Provided Arn is not in correct format.");
        }

        if (!string.Equals(parts[2], "sqs", StringComparison.Ordinal))
        {
            throw new InternalValidationException($"{parts[2]} is not supported as a dead letter resource.");
        }
    }

    /// <summary>A <c>LogConfig</c> is set as a whole, so it must name both of its settings.</summary>
    private static void ValidateLogConfig(LogConfig? config)
    {
        if (config is null)
        {
            return;
        }

        if (string.IsNullOrEmpty(config.IncludeDetail))
        {
            throw new InternalValidationException("Parameter(s) IncludeDetail must be specified.");
        }

        if (string.IsNullOrEmpty(config.Level))
        {
            throw new InternalValidationException("Parameter(s) Level must be specified.");
        }
    }

    /// <summary>One API-model constraint violation, as EventBridge words it.</summary>
    private static string Violation(string value, string member, string constraint) =>
        $"Value '{value}' at '{member}' failed to satisfy constraint: {constraint}";

    private static void ThrowIfViolations(List<string> violations)
    {
        if (violations.Count > 0)
        {
            var count = violations.Count == 1 ? "1 validation error" : $"{violations.Count} validation errors";
            throw new InternalValidationException($"{count} detected: {string.Join("; ", violations)}");
        }
    }

    private static string? NullIfEmpty(string? value) => string.IsNullOrEmpty(value) ? null : value;

    /// <summary>A token naming the first bus of the next page.</summary>
    private static string EncodeListEventBusesToken(string nextBusName) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(ListEventBusesTokenPrefix + nextBusName));

    /// <summary>The bus name a token issued by <see cref="EncodeListEventBusesToken"/> names; refuses any other token.</summary>
    private static string? DecodeListEventBusesToken(string? token)
    {
        if (string.IsNullOrEmpty(token))
        {
            return null;
        }

        string decoded;
        try
        {
            decoded = Encoding.UTF8.GetString(Convert.FromBase64String(token));
        }
        catch (FormatException)
        {
            decoded = "";
        }

        if (!decoded.StartsWith(ListEventBusesTokenPrefix, StringComparison.Ordinal))
        {
            throw new InternalValidationException("Parameter NextToken is not valid.");
        }

        return decoded[ListEventBusesTokenPrefix.Length..];
    }
}
