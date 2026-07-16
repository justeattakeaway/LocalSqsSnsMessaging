using System.Collections.Concurrent;
using System.Globalization;
using System.Numerics;
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

    /// <summary>
    /// Number of in-flight (received-but-not-deleted) messages per FIFO message group.
    /// A group with a non-zero count is locked: real SQS FIFO delivers no further
    /// messages from a group until every received message is deleted or its visibility
    /// timeout expires, which is what preserves ordering under concurrent consumers.
    /// Mutated only while holding the corresponding <see cref="MessageGroupLocks"/> entry.
    /// </summary>
    public ConcurrentDictionary<string, int> InFlightGroupCounts { get; } = new();

    /// <summary>
    /// Wake-up signal for FIFO long polling. FIFO messages live in per-group queues rather
    /// than the <see cref="Messages"/> channel, so a waiting receive has no channel reader
    /// to await; instead it waits on this channel, which is pulsed whenever a message may
    /// have become deliverable (enqueue, redelivery, or a delete unlocking a group).
    /// Bounded at 1 with DropWrite: a single pending item is enough to wake all waiters,
    /// and every waiter re-checks the actual group queues after waking.
    /// </summary>
    public Channel<bool> FifoMessageAvailableSignal { get; } = Channel.CreateBounded<bool>(
        new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });

    public void SignalFifoMessageAvailable()
    {
        FifoMessageAvailableSignal.Writer.TryWrite(true);
    }

    /// <summary>
    /// Appends a message to its FIFO message group, assigning a <c>SequenceNumber</c>
    /// system attribute if the message does not already carry one (SNS fan-out assigns
    /// it at publish time). All group mutations happen under the group's lock so enqueue
    /// order, receive order, and redelivery re-insertion stay consistent.
    /// </summary>
    public void EnqueueFifoMessage(string messageGroupId, Message message)
    {
        var groupLock = MessageGroupLocks.GetOrAdd(messageGroupId, _ => new object());
        lock (groupLock)
        {
            message.Attributes ??= [];
            if (!message.Attributes.ContainsKey("SequenceNumber"))
            {
                message.Attributes["SequenceNumber"] =
                    FifoSequenceNumber.Next().ToString(NumberFormatInfo.InvariantInfo);
            }

            MessageGroups.AddOrUpdate(messageGroupId,
                _ => new ConcurrentQueue<Message>([message]),
                (_, existingQueue) =>
                {
                    existingQueue.Enqueue(message);
                    return existingQueue;
                });
        }

        SignalFifoMessageAvailable();
    }

    /// <summary>
    /// Makes an in-flight message visible again (visibility timeout expired, or visibility
    /// explicitly set to zero). Standard-queue messages go back onto the channel; FIFO
    /// messages are re-inserted into their group in sequence-number order — i.e. at the
    /// head, ahead of anything sent after them — so redelivery preserves group ordering.
    /// Also releases the message's in-flight slot, unlocking the group.
    /// </summary>
    public void ReturnInFlightMessage(Message message)
    {
        string? groupId = null;
        if (IsFifo)
        {
            message.Attributes?.TryGetValue("MessageGroupId", out groupId);
        }

        if (string.IsNullOrEmpty(groupId))
        {
            Messages.Writer.TryWrite(message);
            return;
        }

        var groupLock = MessageGroupLocks.GetOrAdd(groupId!, _ => new object());
        lock (groupLock)
        {
            var items = MessageGroups.TryGetValue(groupId!, out var existingQueue)
                ? new List<Message>(existingQueue)
                : [];

            var sequence = GetSequenceNumber(message);
            var insertAt = items.FindIndex(m => GetSequenceNumber(m) > sequence);
            if (insertAt < 0)
            {
                items.Add(message);
            }
            else
            {
                items.Insert(insertAt, message);
            }

            // Receivers re-fetch the group queue under the lock, so swapping in a rebuilt
            // queue (ConcurrentQueue has no push-front) is safe.
            MessageGroups[groupId!] = new ConcurrentQueue<Message>(items);

            ReleaseInFlightGroupSlotCore(groupId!);
        }

        SignalFifoMessageAvailable();
    }

    /// <summary>
    /// Releases the in-flight slot a deleted FIFO message held, unlocking its message
    /// group for subsequent receives, and drops the group's bookkeeping if it is empty.
    /// </summary>
    public void ReleaseDeletedFifoMessage(Message message)
    {
        if (message.Attributes?.TryGetValue("MessageGroupId", out var groupId) != true || string.IsNullOrEmpty(groupId))
        {
            return;
        }

        var groupLock = MessageGroupLocks.GetOrAdd(groupId, _ => new object());
        lock (groupLock)
        {
            ReleaseInFlightGroupSlotCore(groupId);

            if (MessageGroups.TryGetValue(groupId, out var groupQueue) && groupQueue.IsEmpty)
            {
                MessageGroups.TryRemove(groupId, out _);
            }
        }

        // The group may have become deliverable again for a waiting receive.
        SignalFifoMessageAvailable();
    }

    private void ReleaseInFlightGroupSlotCore(string groupId)
    {
        if (InFlightGroupCounts.TryGetValue(groupId, out var count))
        {
            if (count <= 1)
            {
                InFlightGroupCounts.TryRemove(groupId, out _);
            }
            else
            {
                InFlightGroupCounts[groupId] = count - 1;
            }
        }
    }

    private static BigInteger GetSequenceNumber(Message message)
    {
        return message.Attributes?.TryGetValue("SequenceNumber", out var sequence) == true
               && BigInteger.TryParse(sequence, NumberStyles.None, NumberFormatInfo.InvariantInfo, out var value)
            ? value
            : BigInteger.MinusOne;
    }
}
