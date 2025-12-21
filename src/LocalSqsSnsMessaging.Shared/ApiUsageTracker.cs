using System.Collections.Concurrent;
using System.Text.Json;

namespace LocalSqsSnsMessaging;

/// <summary>
/// Tracks API operations executed against the in-memory AWS bus.
/// Provides methods to query usage and generate IAM policy documents.
/// </summary>
public sealed class ApiUsageTracker
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    private ConcurrentBag<ApiOperation> _operations = [];

    /// <summary>
    /// Gets all recorded operations.
    /// </summary>
    public IReadOnlyList<ApiOperation> Operations => [.. _operations];

    /// <summary>
    /// Records an API operation.
    /// </summary>
    internal void RecordOperation(ApiOperation operation)
    {
        _operations.Add(operation);
    }

    /// <summary>
    /// Gets all unique actions that were used, in IAM format (e.g., "sqs:SendMessage").
    /// </summary>
    /// <returns>A collection of unique action strings.</returns>
    public IEnumerable<string> GetUsedActions()
    {
        return _operations
            .Select(op => $"{op.Service}:{op.Action}")
            .Distinct()
            .OrderBy(a => a);
    }

    /// <summary>
    /// Gets all unique resource ARNs that were accessed.
    /// </summary>
    /// <returns>A collection of unique resource ARN strings.</returns>
    public IEnumerable<string> GetAccessedResources()
    {
        return _operations
            .Where(op => op.ResourceArn is not null)
            .Select(op => op.ResourceArn!)
            .Distinct()
            .OrderBy(a => a);
    }

    /// <summary>
    /// Gets all unique actions for a specific service (e.g., "sqs" or "sns").
    /// </summary>
    /// <param name="service">The service name to filter by.</param>
    /// <returns>A collection of unique action strings for the specified service.</returns>
    public IEnumerable<string> GetUsedActionsForService(string service)
    {
        return _operations
            .Where(op => op.Service.Equals(service, StringComparison.OrdinalIgnoreCase))
            .Select(op => $"{op.Service}:{op.Action}")
            .Distinct()
            .OrderBy(a => a);
    }

    /// <summary>
    /// Gets all unique resource ARNs for a specific service (e.g., "sqs" or "sns").
    /// </summary>
    /// <param name="service">The service name to filter by.</param>
    /// <returns>A collection of unique resource ARN strings for the specified service.</returns>
    public IEnumerable<string> GetAccessedResourcesForService(string service)
    {
        return _operations
            .Where(op => op.Service.Equals(service, StringComparison.OrdinalIgnoreCase) && op.ResourceArn is not null)
            .Select(op => op.ResourceArn!)
            .Distinct()
            .OrderBy(a => a);
    }

    /// <summary>
    /// Generates a minimal IAM policy JSON document covering all observed API usage.
    /// Groups actions by service and includes all accessed resources.
    /// </summary>
    /// <returns>A JSON string representing an IAM policy document.</returns>
    public string GenerateIamPolicyJson()
    {
        var statements = new List<object>();

        // Group by service (using ordinal comparison for grouping key)
#pragma warning disable CA1308 // Normalize strings to uppercase - IAM service names are lowercase by convention
        var serviceGroups = _operations
            .GroupBy(op => op.Service.ToLowerInvariant())
            .OrderBy(g => g.Key);
#pragma warning restore CA1308

        foreach (var serviceGroup in serviceGroups)
        {
            var actions = serviceGroup
                .Select(op => $"{op.Service}:{op.Action}")
                .Distinct()
                .OrderBy(a => a)
                .ToList();

            var resources = serviceGroup
                .Where(op => op.ResourceArn is not null)
                .Select(op => op.ResourceArn!)
                .Distinct()
                .OrderBy(r => r)
                .ToList();

            // If no specific resources, use "*"
            if (resources.Count == 0)
            {
                resources.Add("*");
            }

            statements.Add(new
            {
                Effect = "Allow",
                Action = actions,
                Resource = resources
            });
        }

        var policy = new
        {
            Version = "2012-10-17",
            Statement = statements
        };

        return JsonSerializer.Serialize(policy, JsonOptions);
    }

    /// <summary>
    /// Generates IAM policy statements as structured objects.
    /// Useful for programmatic manipulation of the policy.
    /// </summary>
    /// <returns>A list of IAM statement objects.</returns>
    public IReadOnlyList<IamStatement> GenerateIamStatements()
    {
        var statements = new List<IamStatement>();

#pragma warning disable CA1308 // Normalize strings to uppercase - IAM service names are lowercase by convention
        var serviceGroups = _operations
            .GroupBy(op => op.Service.ToLowerInvariant())
            .OrderBy(g => g.Key);
#pragma warning restore CA1308

        foreach (var serviceGroup in serviceGroups)
        {
            var actions = serviceGroup
                .Select(op => $"{op.Service}:{op.Action}")
                .Distinct()
                .OrderBy(a => a)
                .ToList();

            var resources = serviceGroup
                .Where(op => op.ResourceArn is not null)
                .Select(op => op.ResourceArn!)
                .Distinct()
                .OrderBy(r => r)
                .ToList();

            if (resources.Count == 0)
            {
                resources.Add("*");
            }

            statements.Add(new IamStatement
            {
                Effect = "Allow",
                Actions = actions,
                Resources = resources
            });
        }

        return statements;
    }

    /// <summary>
    /// Clears all recorded operations.
    /// </summary>
    public void Clear()
    {
        // ConcurrentBag.Clear() is not available in netstandard2.0, so we replace the bag
        _operations = [];
    }
}

/// <summary>
/// Represents an IAM policy statement.
/// </summary>
public sealed class IamStatement
{
    /// <summary>
    /// Gets or sets the effect of the statement (typically "Allow" or "Deny").
    /// </summary>
    public required string Effect { get; init; }

    /// <summary>
    /// Gets or sets the list of actions in the statement.
    /// </summary>
    public required IReadOnlyList<string> Actions { get; init; }

    /// <summary>
    /// Gets or sets the list of resource ARNs in the statement.
    /// </summary>
    public required IReadOnlyList<string> Resources { get; init; }
}
