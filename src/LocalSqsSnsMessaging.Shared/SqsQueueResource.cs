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

    /// <summary>The queue's <c>MessageRetentionPeriod</c>: how long after it was sent a message is discarded.</summary>
    public TimeSpan MessageRetentionPeriod { get; set; } = TimeSpan.FromDays(4);

    /// <summary>
    /// Whether <paramref name="message"/> has outlived <see cref="MessageRetentionPeriod"/>, counted
    /// from its <c>SentTimestamp</c>. Retention applies wherever the message is, in flight included.
    /// </summary>
    public bool IsExpired(Message message, DateTimeOffset now)
    {
        return message.Attributes?.TryGetValue("SentTimestamp", out var sentTimestamp) == true
               && long.TryParse(sentTimestamp, NumberStyles.None, NumberFormatInfo.InvariantInfo, out var sentMilliseconds)
               && DateTimeOffset.FromUnixTimeMilliseconds(sentMilliseconds) + MessageRetentionPeriod <= now;
    }

    /// <summary>
    /// Discards every message that has outlived the retention period, waiting or in flight, so the
    /// approximate counters stop reporting them. Delivery checks expiry per message as well, so
    /// this only needs to run before the counters are read.
    /// </summary>
    public void RemoveExpiredMessages(DateTimeOffset now)
    {
        Messages.RemoveWhere(message => IsExpired(message, now));

        foreach (var groupId in MessageGroups.Keys)
        {
            var groupLock = MessageGroupLocks.GetOrAdd(groupId, _ => new object());
            lock (groupLock)
            {
                if (MessageGroups.TryGetValue(groupId, out var groupQueue) && groupQueue.Any(message => IsExpired(message, now)))
                {
                    MessageGroups[groupId] = new ConcurrentQueue<Message>(groupQueue.Where(message => !IsExpired(message, now)));
                }
            }
        }

        foreach (var (receiptHandle, (message, expirationJob)) in InFlightMessages)
        {
            if (IsExpired(message, now) && InFlightMessages.TryRemove(receiptHandle, out _))
            {
                LatestReceiptHandles.TryRemove(message.MessageId!, out _);
                expirationJob.Dispose();
                if (IsFifo)
                {
                    ReleaseDeletedFifoMessage(message);
                }
            }
        }
    }

    /// <summary>The queue's <c>DelaySeconds</c>: the delay applied to a message sent without its own.</summary>
    public int DelaySeconds { get; set; }

    /// <summary>The queue's <c>ReceiveMessageWaitTimeSeconds</c>: the long-poll wait of a receive that sets none.</summary>
    public TimeSpan ReceiveMessageWaitTime { get; set; }

    public DateTimeOffset CreatedTimestamp { get; init; }
    public DateTimeOffset LastModifiedTimestamp { get; set; }

    /// <summary>
    /// Messages sent with a delay that has not elapsed yet. A message leaves the set when its delay
    /// ends and it becomes visible, or when a purge discards it first.
    /// </summary>
    private readonly ConcurrentDictionary<Message, byte> _delayedMessages = new(ReferenceEqualityComparer.Instance);

    /// <summary>Messages sent with a delay that has not elapsed yet (<c>ApproximateNumberOfMessagesDelayed</c>).</summary>
    public int DelayedMessageCount => _delayedMessages.Count;

    /// <summary>
    /// Visible messages (<c>ApproximateNumberOfMessages</c>): the standard-queue store plus, for FIFO
    /// queues, every message waiting in a message group.
    /// </summary>
    public int VisibleMessageCount => Messages.Count + MessageGroups.Values.Sum(group => group.Count);
    public string Arn => $"arn:aws:sqs:{Region}:{AccountId}:{Name}";
    public SqsQueueResource? ErrorQueue { get; set; }
    public int? MaxReceiveCount { get; set; }
    public Dictionary<string, string>? Attributes { get; set; }
    public Dictionary<string, string>? Tags { get; set; }

    /// <summary>Guards <see cref="Tags"/>: concurrent TagQueue/UntagQueue calls must not lose updates.</summary>
    public object TagsLock { get; } = new();
    public SqsMessageStore Messages { get; } = new();

    /// <summary>
    /// The receipt handle each message was most recently received with, by MessageId. Only that
    /// handle can act on the message: after its visibility timeout lapses it still re-hides or
    /// deletes it, while an older handle is stale. The entry goes when the message leaves the queue.
    /// </summary>
    public ConcurrentDictionary<string, string> LatestReceiptHandles { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// Takes a visible (waiting, not in flight) message out of the queue by id, so a lapsed receipt
    /// handle can delete or re-hide it.
    /// </summary>
    public bool TryTakeVisibleMessage(string messageId, out Message? message)
    {
        Message? found = null;
        Messages.RemoveWhere(candidate =>
        {
            if (found is null && candidate.MessageId == messageId)
            {
                found = candidate;
                return true;
            }

            return false;
        });

        if (found is null && IsFifo)
        {
            foreach (var groupId in MessageGroups.Keys)
            {
                var groupLock = MessageGroupLocks.GetOrAdd(groupId, _ => new object());
                lock (groupLock)
                {
                    if (MessageGroups.TryGetValue(groupId, out var groupQueue)
                        && groupQueue.FirstOrDefault(candidate => candidate.MessageId == messageId) is { } match)
                    {
                        MessageGroups[groupId] = new ConcurrentQueue<Message>(groupQueue.Where(candidate => !ReferenceEquals(candidate, match)));
                        found = match;
                        break;
                    }
                }
            }
        }

        message = found;
        return found is not null;
    }
    public ConcurrentDictionary<string, (Message, SqsInflightMessageExpirationJob)> InFlightMessages { get; } = new();
    public ConcurrentDictionary<string, ConcurrentQueue<Message>> MessageGroups { get; } = new();
    public ConcurrentDictionary<string, object> MessageGroupLocks { get; } = new();
    public ConcurrentDictionary<string, string> DeduplicationIds { get; } = new();

    /// <summary>
    /// The SequenceNumber each FIFO message was assigned at send, by MessageId, so a deduplicated
    /// re-send can report the original's, as SQS does.
    /// </summary>
    public ConcurrentDictionary<string, string> SequenceNumbersByMessageId { get; } = new(StringComparer.Ordinal);
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
    /// than in <see cref="Messages"/>, so a waiting receive has nothing there to park on;
    /// instead it waits on this channel, which is pulsed whenever a message may have become
    /// deliverable (enqueue, redelivery, or a delete unlocking a group). Unlike a standard
    /// queue, which hands a message to one waiting receive, this wakes every waiter: whether
    /// a given receive can take anything depends on which groups are locked, so they all have
    /// to re-check. Bounded at 1 with DropWrite, since one pending pulse wakes everyone.
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
    /// Makes <paramref name="message"/> visible once <paramref name="delay"/> has elapsed, counting it as
    /// delayed until then. A FIFO message (non-null <paramref name="fifoMessageGroupId"/>) joins its group.
    /// </summary>
    public void EnqueueAfterDelay(Message message, TimeSpan delay, TimeProvider timeProvider, string? fifoMessageGroupId = null)
    {
        _delayedMessages.TryAdd(message, 0);
        var start = timeProvider.GetTimestamp();
        _ = EnqueueAfterDelayCore();

        async Task EnqueueAfterDelayCore()
        {
            // Timers can fire a little early; a delayed message must never be visible before
            // its full delay has elapsed.
            var remaining = delay;
            while (remaining > TimeSpan.Zero)
            {
                await timeProvider.Delay(remaining).ConfigureAwait(false);
                remaining = delay - timeProvider.GetElapsedTime(start);
            }

            // Gone already when the queue was purged while the message was still delayed.
            if (!_delayedMessages.TryRemove(message, out _))
            {
                return;
            }

            if (fifoMessageGroupId is null)
            {
                Messages.Enqueue(message);
            }
            else
            {
                EnqueueFifoMessage(fifoMessageGroupId, message);
            }
        }
    }

    /// <summary>Discards every message still waiting out its delay, as <c>PurgeQueue</c> does.</summary>
    public void DiscardDelayedMessages() => _delayedMessages.Clear();

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
            Messages.Enqueue(message);
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
