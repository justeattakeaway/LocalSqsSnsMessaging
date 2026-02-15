using System.Collections.Concurrent;
using System.Threading.Channels;
using LocalSqsSnsMessaging.Sqs.Model;

namespace LocalSqsSnsMessaging;

internal sealed class SqsQueueResource
{
    public required string Name { get; init; }
    public required string Region { get; init; }
    public required string Url { get; init; }
    public required string AccountId { get; init; }
    public bool IsFifo => Name.EndsWith(".fifo", StringComparison.OrdinalIgnoreCase);
    public TimeSpan VisibilityTimeout { get; set; }
    public string Arn => $"arn:aws:sqs:{Region}:{AccountId}:{Name}";
    public SqsQueueResource? ErrorQueue { get; set; }
    public int? MaxReceiveCount { get; set; }
    public Dictionary<string, string>? Attributes { get; set; }
    public Dictionary<string, string>? Tags { get; set; }
    public Channel<Message> Messages { get; } = Channel.CreateUnbounded<Message>();
    public ConcurrentDictionary<string, (Message, SqsInflightMessageExpirationJob)> InFlightMessages { get; } = new();
    public ConcurrentDictionary<string, ConcurrentQueue<Message>> MessageGroups { get; } = new();
    public ConcurrentDictionary<string, object> MessageGroupLocks { get; } = new();
    public ConcurrentDictionary<string, string> DeduplicationIds { get; } = new();
    public ConcurrentDictionary<string, ConcurrentDictionary<string, string>> MessageGroupDeduplicationIds { get; } = new();
}
