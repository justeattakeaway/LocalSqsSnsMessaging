using System.Collections.Concurrent;
using LocalSqsSnsMessaging.EventBridge.Model;

namespace LocalSqsSnsMessaging;

/// <summary>
/// In-memory representation of an EventBridge event bus. Each bus owns its own set of rules.
/// </summary>
/// <remarks>
/// The configuration (description, KMS key, dead-letter queue, logging and resource policy) is
/// read and written under a lock on the resource, so an update is applied as a whole and every
/// read surface sees either all of it or none of it.
/// </remarks>
internal sealed class EventBusResource
{
    public required string Name { get; init; }
    public required string Arn { get; init; }
    public string? Description { get; set; }

    /// <summary>The customer managed KMS key the bus encrypts events with; <see langword="null"/> for an AWS owned key.</summary>
    public string? KmsKeyIdentifier { get; set; }

    /// <summary>The SQS queue that receives events the bus could not deliver, as configured (it is not resolved).</summary>
    public DeadLetterConfig? DeadLetterConfig { get; set; }

    /// <summary>How the bus logs, stored and echoed only: the library writes no logs.</summary>
    public LogConfig? LogConfig { get; set; }

    /// <summary>The bus's resource policy document, or <see langword="null"/> when it has none.</summary>
    public string? Policy { get; set; }

    /// <summary>
    /// When the bus was created; <see langword="null"/> for the account's default bus, which is never
    /// explicitly created, so EventBridge reports no creation or modification time for it.
    /// </summary>
    public DateTimeOffset? CreationTime { get; init; }
    public DateTimeOffset? LastModifiedTime { get; set; }

    /// <summary>The bus's tags.</summary>
    public EventBridgeTagSet Tags { get; init; } = new();

    /// <summary>Rules defined on this bus, keyed by rule name.</summary>
    public ConcurrentDictionary<string, RuleResource> Rules { get; } = new();
}
