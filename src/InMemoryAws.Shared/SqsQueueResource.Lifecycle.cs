using System.Collections.Concurrent;
using InMemoryAws.Sqs.Model;

namespace InMemoryAws;

// Where a queue's messages are, and every way one leaves flight or the queue.
//
// A message is in exactly one place: delayed (SqsQueueResource.cs), waiting (_waiting for a standard
// queue, its FifoGroup for a FIFO queue) or in flight (_inFlight, by receipt handle). Every way out
// of flight goes through LeaveFlight, which stops the visibility timer and releases the FIFO group's
// slot under the group's lock; every way out of the queue goes through Forget, which retires the
// message's receipt handle.
internal sealed partial class SqsQueueResource
{
    /// <summary>A standard queue's waiting messages, and the receives parked waiting for one.</summary>
    private readonly SqsMessageStore _waiting = new();

    /// <summary>A FIFO queue's message groups that hold a waiting or in-flight message.</summary>
    private readonly ConcurrentDictionary<string, FifoGroup> _groups = new(StringComparer.Ordinal);

    /// <summary>Messages received and neither deleted nor visible again yet, by receipt handle.</summary>
    private readonly ConcurrentDictionary<string, InFlightMessage> _inFlight = new(StringComparer.Ordinal);

    /// <summary>
    /// The receipt handle each message was most recently received with, by MessageId. Only that
    /// handle can act on the message: after its visibility timeout lapses it still re-hides or
    /// deletes it, while an older handle is stale. The entry goes when the message leaves the queue.
    /// </summary>
    private readonly ConcurrentDictionary<string, string> _latestReceiptHandles = new(StringComparer.Ordinal);

    /// <summary>
    /// One FIFO message group. Everything in it is guarded by locking the group itself, taken only
    /// through <see cref="InGroup"/> or <see cref="TryInExistingGroup"/>.
    /// </summary>
    private sealed class FifoGroup
    {
        /// <summary>The group's waiting messages, in sequence-number order.</summary>
        public LinkedList<Message> Waiting { get; } = new();

        /// <summary>
        /// Slots held by messages taken from the group and not yet back or gone: received messages,
        /// and one being moved by a move task. A group with a slot held delivers nothing further, as
        /// SQS delivers nothing more from a group until its received messages are deleted or visible
        /// again, which keeps the group in order under concurrent consumers.
        /// </summary>
        public int Held { get; set; }

        /// <summary>Set once the group is idle and removed from the queue; a caller holding it starts over.</summary>
        public bool Retired { get; set; }

        public bool IsIdle => Waiting.Count == 0 && Held == 0;
    }

    private sealed class InFlightMessage(Message message, string? groupId, ITimer visibilityTimer)
    {
        public Message Message { get; } = message;

        /// <summary>The FIFO group whose slot the message holds; <see langword="null"/> on a standard queue.</summary>
        public string? GroupId { get; } = groupId;

        public ITimer VisibilityTimer { get; } = visibilityTimer;
    }

    /// <summary>Runs <paramref name="action"/> on group <paramref name="groupId"/> under its lock, creating the group if need be.</summary>
    private void InGroup(string groupId, Action<FifoGroup> action)
    {
        while (true)
        {
            var group = _groups.GetOrAdd(groupId, static _ => new FifoGroup());
            lock (group)
            {
                if (group.Retired)
                {
                    continue;
                }

                try
                {
                    action(group);
                    return;
                }
                finally
                {
                    RetireIfIdle(groupId, group);
                }
            }
        }
    }

    /// <summary>Runs <paramref name="action"/> on group <paramref name="groupId"/> under its lock, if the group exists.</summary>
    private bool TryInExistingGroup(string groupId, FifoGroup group, Func<FifoGroup, bool> action)
    {
        lock (group)
        {
            if (group.Retired)
            {
                return false;
            }

            try
            {
                return action(group);
            }
            finally
            {
                RetireIfIdle(groupId, group);
            }
        }
    }

    /// <summary>Removes a group that holds nothing. The caller holds the group's lock.</summary>
    private void RetireIfIdle(string groupId, FifoGroup group)
    {
        if (group.IsIdle)
        {
            group.Retired = true;
            ((ICollection<KeyValuePair<string, FifoGroup>>)_groups).Remove(new KeyValuePair<string, FifoGroup>(groupId, group));
        }
    }

    /// <summary>Puts a message that was in flight back in its group, ahead of anything sent after it.</summary>
    private static void InsertInSequence(LinkedList<Message> waiting, Message message)
    {
        var sequence = GetSequenceNumber(message);
        for (var node = waiting.First; node is not null; node = node.Next)
        {
            if (GetSequenceNumber(node.Value) > sequence)
            {
                waiting.AddBefore(node, message);
                return;
            }
        }

        waiting.AddLast(message);
    }

    /// <summary>
    /// Puts a received message in flight under <paramref name="receiptHandle"/>, taking its FIFO
    /// group's slot. On a FIFO queue the caller holds <paramref name="group"/>'s lock.
    /// </summary>
    /// <remarks>
    /// An infinite <paramref name="visibilityTimeout"/> starts no timer: a receive starts it once it
    /// returns (see <see cref="StartVisibilityTimeout"/>).
    /// </remarks>
    private void EnterFlight(Message message, string receiptHandle, TimeSpan visibilityTimeout, string? groupId, FifoGroup? group)
    {
        // Registered before the timer starts: a timer that fires at once must find the message to
        // return and the slot to release.
        var timer = TimeProvider.CreateTimer(
            static state =>
            {
                var (queue, handle) = ((SqsQueueResource, string))state!;
                queue.LeaveFlight(handle, discard: false);
            },
            (this, receiptHandle), Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        _latestReceiptHandles[message.MessageId!] = receiptHandle;
        if (group is not null)
        {
            group.Held++;
        }

        _inFlight[receiptHandle] = new InFlightMessage(message, groupId, timer);
        if (visibilityTimeout != Timeout.InfiniteTimeSpan)
        {
            timer.Change(visibilityTimeout, Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>
    /// Starts the visibility timeout of a message a receive just put in flight; a zero timeout
    /// makes it visible again at once.
    /// </summary>
    private void StartVisibilityTimeout(string receiptHandle, TimeSpan visibilityTimeout)
    {
        if (visibilityTimeout == TimeSpan.Zero)
        {
            LeaveFlight(receiptHandle, discard: false);
        }
        else if (_inFlight.TryGetValue(receiptHandle, out var entry))
        {
            entry.VisibilityTimer.Change(visibilityTimeout, Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>
    /// Takes the message in flight under <paramref name="receiptHandle"/> out of flight: its timer
    /// stops and its FIFO group's slot is released. It becomes visible again, in its place in its
    /// group, unless <paramref name="discard"/> is set or it has outlived the retention period, in
    /// which case it leaves the queue.
    /// </summary>
    /// <returns><see langword="false"/> when no message is in flight under the handle.</returns>
    private bool LeaveFlight(string receiptHandle, bool discard)
    {
        var now = TimeProvider.GetUtcNow();
        if (!_inFlight.TryGetValue(receiptHandle, out var entry))
        {
            return false;
        }

        if (entry.GroupId is not { } groupId)
        {
            if (!_inFlight.TryRemove(receiptHandle, out _))
            {
                return false;
            }

            entry.VisibilityTimer.Dispose();
            var message = entry.Message;
            if (!discard && !IsExpired(message, now))
            {
                _waiting.Enqueue(message);
            }
            else
            {
                Forget(message);
            }

            return true;
        }

        // Out of flight, back in the group and the slot released under one lock, so neither a
        // receive nor a delete finds the message in between.
        var left = false;
        InGroup(groupId, group =>
        {
            if (!_inFlight.TryRemove(receiptHandle, out _))
            {
                return;
            }

            left = true;
            entry.VisibilityTimer.Dispose();
            group.Held--;
            if (!discard && !IsExpired(entry.Message, now))
            {
                InsertInSequence(group.Waiting, entry.Message);
            }
            else
            {
                Forget(entry.Message);
            }
        });

        if (left)
        {
            SignalFifoMessageAvailable();
        }

        return left;
    }

    /// <summary>The message has left the queue: no receipt handle acts on it any more.</summary>
    private void Forget(Message message) => _latestReceiptHandles.TryRemove(message.MessageId!, out _);

    /// <summary>
    /// Takes a waiting message out of the queue by id, running <paramref name="whileHeld"/> on it
    /// before anything else can reach its group (under the group's lock on a FIFO queue).
    /// </summary>
    private bool TryTakeWaiting(string messageId, Action<Message, string?, FifoGroup?> whileHeld)
    {
        if (!IsFifo)
        {
            Message? found = null;
            _waiting.RemoveWhere(candidate =>
            {
                if (found is null && string.Equals(candidate.MessageId, messageId, StringComparison.Ordinal))
                {
                    found = candidate;
                    return true;
                }

                return false;
            });

            if (found is null)
            {
                return false;
            }

            whileHeld(found, null, null);
            return true;
        }

        foreach (var (groupId, group) in _groups)
        {
            var taken = TryInExistingGroup(groupId, group, held =>
            {
                for (var node = held.Waiting.First; node is not null; node = node.Next)
                {
                    if (string.Equals(node.Value.MessageId, messageId, StringComparison.Ordinal))
                    {
                        held.Waiting.Remove(node);
                        whileHeld(node.Value, groupId, held);
                        return true;
                    }
                }

                return false;
            });

            if (taken)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>What a receipt handle refers to on this queue.</summary>
    private enum ReceiptHandleState
    {
        /// <summary>The message is in flight under this handle.</summary>
        Current,

        /// <summary>The handle is the message's latest, but its visibility timeout lapsed: the message is visible again.</summary>
        Lapsed,

        /// <summary>The message has been received again since, under a newer handle.</summary>
        Stale,

        /// <summary>The handle was minted by this queue, but its message is no longer here (deleted, expired, moved).</summary>
        Gone,

        /// <summary>A well-formed handle minted by another queue.</summary>
        Foreign,

        /// <summary>Not a receipt handle.</summary>
        Invalid,

        /// <summary>A handle from an earlier queue of the same name, since deleted.</summary>
        DeletedQueue,
    }

    private ReceiptHandleState ResolveReceiptHandle(string? receiptHandle, out string messageId, out DateTimeOffset receivedAt)
    {
        messageId = string.Empty;
        receivedAt = default;
        if (receiptHandle is null || !SqsReceiptHandle.TryParse(receiptHandle, out var queueArn, out messageId, out receivedAt, out var incarnation))
        {
            return ReceiptHandleState.Invalid;
        }

        if (_inFlight.ContainsKey(receiptHandle))
        {
            return ReceiptHandleState.Current;
        }

        if (!queueArn.Equals(Arn, StringComparison.OrdinalIgnoreCase))
        {
            return ReceiptHandleState.Foreign;
        }

        if (!string.Equals(incarnation, Incarnation, StringComparison.Ordinal))
        {
            return ReceiptHandleState.DeletedQueue;
        }

        if (!_latestReceiptHandles.TryGetValue(messageId, out var latestReceiptHandle))
        {
            return ReceiptHandleState.Gone;
        }

        return string.Equals(latestReceiptHandle, receiptHandle, StringComparison.Ordinal)
            ? ReceiptHandleState.Lapsed
            : ReceiptHandleState.Stale;
    }

    private static SqsReceiptHandleOutcome Refusal(ReceiptHandleState state) => state switch
    {
        ReceiptHandleState.Foreign => SqsReceiptHandleOutcome.NotThisQueue,
        ReceiptHandleState.DeletedQueue => SqsReceiptHandleOutcome.QueueDeleted,
        _ => SqsReceiptHandleOutcome.Invalid,
    };

    /// <summary>
    /// Deletes the message a receipt handle refers to, as SQS does: the current or lapsed handle
    /// deletes it; a handle whose message is already gone succeeds; a stale handle succeeds without
    /// deleting on a standard queue and is refused as expired on a FIFO queue. The deduplication id
    /// stays claimed for the rest of its interval.
    /// </summary>
    public SqsReceiptHandleOutcome DeleteMessage(string? receiptHandle)
    {
        var state = ResolveReceiptHandle(receiptHandle, out var messageId, out _);
        switch (state)
        {
            case ReceiptHandleState.Current:
                MarkReceiveAttemptModified(receiptHandle!);
                if (LeaveFlight(receiptHandle!, discard: true))
                {
                    return SqsReceiptHandleOutcome.Done;
                }

                // Its visibility timeout ended meanwhile: the handle has just lapsed.
                goto case ReceiptHandleState.Lapsed;
            case ReceiptHandleState.Lapsed:
                MarkReceiveAttemptModified(receiptHandle!);
                TryTakeWaiting(messageId, (message, _, _) => Forget(message));
                return SqsReceiptHandleOutcome.Done;
            case ReceiptHandleState.Stale:
                return IsFifo ? SqsReceiptHandleOutcome.Expired : SqsReceiptHandleOutcome.Done;
            case ReceiptHandleState.Gone:
                return SqsReceiptHandleOutcome.Done;
            default:
                return Refusal(state);
        }
    }

    /// <summary>
    /// Changes the visibility timeout of the message a receipt handle refers to, as SQS does: the
    /// current handle restarts it (zero makes the message visible at once); a lapsed handle hides
    /// the visible message again; a stale handle, or one whose message is gone, is refused. The
    /// message can stay in flight at most 12 hours from the handle's receive.
    /// </summary>
    /// <param name="receiptHandle">The handle a receive returned.</param>
    /// <param name="visibilityTimeout">The new timeout, from now; already checked to be within SQS's range.</param>
    public SqsReceiptHandleOutcome ChangeVisibility(string? receiptHandle, TimeSpan visibilityTimeout)
    {
        var state = ResolveReceiptHandle(receiptHandle, out var messageId, out var receivedAt);
        if (state is ReceiptHandleState.Current or ReceiptHandleState.Lapsed)
        {
            if (TimeProvider.GetUtcNow() - receivedAt + visibilityTimeout > TimeSpan.FromSeconds(SqsRequestValidation.MaxVisibilityTimeoutSeconds))
            {
                return SqsReceiptHandleOutcome.VisibilityCapExceeded;
            }

            MarkReceiveAttemptModified(receiptHandle!);
        }

        switch (state)
        {
            case ReceiptHandleState.Current:
                if (visibilityTimeout == TimeSpan.Zero)
                {
                    // Visible again, now or because its timeout ended meanwhile.
                    LeaveFlight(receiptHandle!, discard: false);
                    return SqsReceiptHandleOutcome.Done;
                }

                if (_inFlight.TryGetValue(receiptHandle!, out var entry)
                    && entry.VisibilityTimer.Change(visibilityTimeout, Timeout.InfiniteTimeSpan))
                {
                    return SqsReceiptHandleOutcome.Done;
                }

                // Its visibility timeout ended meanwhile: the handle has just lapsed.
                goto case ReceiptHandleState.Lapsed;
            case ReceiptHandleState.Lapsed:
                if (visibilityTimeout > TimeSpan.Zero)
                {
                    // The handle is still the message's latest, so it hides the message again.
                    TryTakeWaiting(messageId, (message, groupId, group) =>
                        EnterFlight(message, receiptHandle!, visibilityTimeout, groupId, group));
                }

                return SqsReceiptHandleOutcome.Done;
            case ReceiptHandleState.Stale:
                return SqsReceiptHandleOutcome.Expired;
            case ReceiptHandleState.Gone:
                return SqsReceiptHandleOutcome.MessageNotAvailable;
            default:
                return Refusal(state);
        }
    }

    /// <summary>
    /// Discards every message, waiting, delayed or in flight, and forgets every deduplication id,
    /// receipt handle and receive attempt, as <c>PurgeQueue</c> does.
    /// </summary>
    public void Purge()
    {
        foreach (var receiptHandle in _inFlight.Keys)
        {
            LeaveFlight(receiptHandle, discard: true);
        }

        // Each message's handle is retired with it, not all at once: a message a concurrent receive
        // puts in flight survives the purge with its handle intact.
        _waiting.RemoveWhere(message =>
        {
            Forget(message);
            return true;
        });
        foreach (var (groupId, group) in _groups)
        {
            TryInExistingGroup(groupId, group, held =>
            {
                foreach (var message in held.Waiting)
                {
                    Forget(message);
                }

                held.Waiting.Clear();
                return true;
            });
        }

        DiscardDelayedMessages();
        ClearDeduplication();
        _receiveAttempts.Clear();
        _receiveAttemptsByReceiptHandle.Clear();
    }

    /// <summary>
    /// Removes one message by id, waiting or in flight, as the dashboard's delete does. An in-flight
    /// message leaves flight as on <see cref="DeleteMessage"/>.
    /// </summary>
    /// <returns><see langword="false"/> when no waiting or in-flight message has that id.</returns>
    public bool RemoveMessage(string messageId)
    {
        foreach (var (receiptHandle, entry) in _inFlight)
        {
            if (string.Equals(entry.Message.MessageId, messageId, StringComparison.Ordinal))
            {
                MarkReceiveAttemptModified(receiptHandle);
                if (LeaveFlight(receiptHandle, discard: true))
                {
                    return true;
                }
            }
        }

        return TryTakeWaiting(messageId, (message, _, _) => Forget(message));
    }

    /// <summary>How many messages the queue holds, after discarding those past the retention period.</summary>
    /// <param name="Visible">Waiting to be received (<c>ApproximateNumberOfMessages</c>).</param>
    /// <param name="InFlight">Received and not yet deleted or visible again (<c>ApproximateNumberOfMessagesNotVisible</c>).</param>
    /// <param name="Delayed">Waiting out a delay (<c>ApproximateNumberOfMessagesDelayed</c>).</param>
    internal readonly record struct MessageCounts(int Visible, int InFlight, int Delayed);

    /// <summary>Counts the queue's messages, first discarding every one that has outlived the retention period.</summary>
    public MessageCounts CountMessages()
    {
        RemoveExpiredMessages(TimeProvider.GetUtcNow());
        var visible = _waiting.Count;
        foreach (var (groupId, group) in _groups)
        {
            TryInExistingGroup(groupId, group, held =>
            {
                visible += held.Waiting.Count;
                return true;
            });
        }

        return new MessageCounts(visible, _inFlight.Count, _delayedMessages.Count);
    }

    /// <summary>
    /// Discards every message that has outlived the retention period, waiting or in flight. Receive
    /// checks expiry per message as well, so this only needs to run before the counters are read.
    /// </summary>
    private void RemoveExpiredMessages(DateTimeOffset now)
    {
        _waiting.RemoveWhere(message =>
        {
            if (!IsExpired(message, now))
            {
                return false;
            }

            Forget(message);
            return true;
        });

        foreach (var (groupId, group) in _groups)
        {
            TryInExistingGroup(groupId, group, held =>
            {
                for (var node = held.Waiting.First; node is not null;)
                {
                    var next = node.Next;
                    if (IsExpired(node.Value, now))
                    {
                        held.Waiting.Remove(node);
                        Forget(node.Value);
                    }

                    node = next;
                }

                return true;
            });
        }

        foreach (var (receiptHandle, entry) in _inFlight)
        {
            if (IsExpired(entry.Message, now))
            {
                LeaveFlight(receiptHandle, discard: true);
            }
        }
    }

    /// <summary>A point-in-time copy of the queue's messages, for the dashboard. Takes nothing out.</summary>
    /// <param name="Waiting">Waiting to be received: in order on a standard queue, group by group on a FIFO queue.</param>
    /// <param name="InFlight">Received and not yet deleted or visible again.</param>
    internal readonly record struct Contents(List<Message> Waiting, List<Message> InFlight);

    public Contents Peek()
    {
        var waiting = _waiting.Snapshot().ConvertAll(CopyOf);
        foreach (var (groupId, group) in _groups)
        {
            TryInExistingGroup(groupId, group, held =>
            {
                waiting.AddRange(held.Waiting.Select(CopyOf));
                return true;
            });
        }

        return new Contents(waiting, [.. _inFlight.Values.Select(entry => CopyOf(entry.Message))]);
    }

    /// <summary>
    /// Takes visible messages out of the queue, oldest first (on a FIFO queue, group by group in
    /// sequence order, skipping groups with a message in flight), handing each to
    /// <paramref name="deliver"/>, as a move task does. Stops after <paramref name="limit"/>
    /// messages, when no visible message is left, or when <paramref name="deliver"/> refuses one,
    /// which stays in the queue.
    /// </summary>
    /// <param name="limit">The most messages to move; <see langword="null"/> for no limit.</param>
    /// <param name="deliver">Puts the message where it is going; <see langword="false"/> to leave it here and stop.</param>
    /// <returns>How many messages were delivered.</returns>
    /// <remarks>
    /// <paramref name="deliver"/> runs with no lock of this queue held, so it can deliver to any
    /// queue, this one's own dead-letter queue included. A FIFO message's group stays held while it
    /// is out, so no receive overtakes it.
    /// </remarks>
    public int MoveMessages(int? limit, Func<Message, bool> deliver)
    {
        var now = TimeProvider.GetUtcNow();
        var moved = 0;
        if (!IsFifo)
        {
            while ((limit is null || moved < limit) && _waiting.TryDequeue(out var message))
            {
                if (IsExpired(message, now))
                {
                    Forget(message);
                    continue;
                }

                if (!deliver(message))
                {
                    _waiting.Enqueue(message);
                    return moved;
                }

                Forget(message);
                moved++;
            }

            return moved;
        }

        foreach (var (groupId, group) in _groups)
        {
            while (limit is null || moved < limit)
            {
                Message? message = null;
                TryInExistingGroup(groupId, group, held =>
                {
                    if (held.Held > 0 || held.Waiting.First is not { } head)
                    {
                        return false;
                    }

                    held.Waiting.RemoveFirst();
                    held.Held++;
                    message = head.Value;
                    return true;
                });

                if (message is null)
                {
                    break;
                }

                var delivered = !IsExpired(message, now) && deliver(message);
                var refused = !delivered && !IsExpired(message, now);
                InGroup(groupId, held =>
                {
                    held.Held--;
                    if (refused)
                    {
                        held.Waiting.AddFirst(message);
                    }
                    else
                    {
                        Forget(message);
                    }
                });

                if (refused)
                {
                    SignalFifoMessageAvailable();
                    return moved;
                }

                if (delivered)
                {
                    moved++;
                }
            }
        }

        SignalFifoMessageAvailable();
        return moved;
    }

    private static Message CopyOf(Message source) => new()
    {
        MessageId = source.MessageId,
        Body = source.Body,
        MD5OfBody = source.MD5OfBody,
        ReceiptHandle = source.ReceiptHandle,
        Attributes = source.Attributes.ToInitializedDictionary(),
        MessageAttributes = source.MessageAttributes.ToInitializedDictionary(),
        MD5OfMessageAttributes = source.MD5OfMessageAttributes
    };
}
