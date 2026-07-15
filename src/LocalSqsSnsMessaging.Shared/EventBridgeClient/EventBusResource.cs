using System.Collections.Concurrent;

namespace LocalSqsSnsMessaging;

/// <summary>
/// In-memory representation of an EventBridge event bus. Each bus owns its own set of rules.
/// </summary>
internal sealed class EventBusResource
{
    public required string Name { get; init; }
    public required string Arn { get; init; }
    public string? Description { get; set; }
    public DateTimeOffset CreationTime { get; init; }
    public DateTimeOffset LastModifiedTime { get; set; }

    /// <summary>Rules defined on this bus, keyed by rule name.</summary>
    public ConcurrentDictionary<string, RuleResource> Rules { get; } = new();
}
