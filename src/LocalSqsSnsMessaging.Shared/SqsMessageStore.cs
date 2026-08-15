// Adapted from UnboundedChannel<T> in dotnet/runtime
// (src/libraries/System.Threading.Channels/src/System/Threading/Channels/UnboundedChannel.cs),
// which is licensed to the .NET Foundation under the MIT license.
//
// The structure is the original's: a ConcurrentQueue holding the items, a lock taken on that
// queue guarding an intrusive linked list of parked readers, and writers handing work to a
// reader from inside that lock. What changed is who gets woken, and what they are woken with:
//
//   - UnboundedChannel has two reader lists. ReadAsync parks on the blocked-reader list and a
//     write hands the item to exactly one of them; WaitToReadAsync parks on the waiting-reader
//     list and a write wakes every one of them to race for the item. Racing is what this store
//     exists to avoid - over HTTP the continuation scheduling that decides the race can settle,
//     and one consumer takes every message - so an enqueue uses the hand-off model: the message
//     itself is given to exactly one parked receive, chosen at random rather than in the
//     original's strict order. The message never touches the item queue on that path, so no
//     other consumer can take it out from under the receive it was given to. A receive that
//     wants more than one message tops up from the item queue after its hand-off, and one that
//     rejects its message (routing it to a dead-letter queue instead) just waits again.
//   - Completion has no meaning for a queue that lives as long as its resource, so the
//     _doneWriting / Completion machinery is gone, and with it the failure paths on write.
//   - Parked receives use a TaskCompletionSource rather than the original's pooled
//     IValueTaskSource operations. That is an allocation per park, against an HTTP round trip
//     per receive; the pooling is not worth the intricacy at this scale.

using System.Collections.Concurrent;
using LocalSqsSnsMessaging.Sqs.Model;

namespace LocalSqsSnsMessaging;

/// <summary>
/// The messages waiting to be received from a standard queue, and the receives parked waiting
/// for one. FIFO queues keep their messages in per-group queues instead (see
/// <see cref="SqsQueueResource.MessageGroups"/>), because delivery there is ordered by group
/// rather than shared out between consumers.
/// </summary>
/// <remarks>
/// Enqueuing is the only way in. It hands the message to a parked receive when there is one,
/// and only otherwise adds it to the waiting messages; parking re-checks the waiting messages
/// under the same lock. So a message is either owned by exactly one receive or visible to any,
/// and no delivery path can add one that waiting consumers sleep through.
/// </remarks>
internal sealed class SqsMessageStore
{
    private readonly ConcurrentQueue<Message> _items = new();

    /// <summary>Parked receives, most recently parked first. Guarded by <see cref="SyncObj"/>.</summary>
    private Waiter? _waitersHead;

    /// <summary>Number of entries in <see cref="_waitersHead"/>. Guarded by <see cref="SyncObj"/>.</summary>
    private int _waiterCount;

#if !NET
    private static readonly Random SharedRandom = new();
#endif

    /// <summary>
    /// Synchronises the waiter list against enqueues. Locking the item queue itself is how
    /// <c>UnboundedChannel</c> does it - the queue is private, so nothing outside can contend.
    /// </summary>
    private object SyncObj => _items;

    /// <summary>Messages available to be received. Approximate under concurrent access.</summary>
    public int Count => _items.Count;

    /// <summary>
    /// Hands the message to one parked receive, chosen at random; when none is parked, adds it
    /// to the waiting messages instead.
    /// </summary>
    public void Enqueue(Message message)
    {
        lock (SyncObj)
        {
            // The hand-off is the message itself, not a bare wake-up. Waking a receive and
            // leaving the message in _items for it to collect would let any other consumer
            // take it first, putting the woken receive back into exactly the race this store
            // exists to remove. TryHand fails only when the receive was cancelled just before
            // the hand-off; it is already on its way out, so pick another.
            //
            // Completing the waiter inside the lock is safe: its continuation is scheduled to
            // the pool (RunContinuationsAsynchronously), never run inline here.
            while (TryTakeRandomWaiter() is { } waiter)
            {
                if (waiter.TryHand(message))
                {
                    return;
                }
            }

            _items.Enqueue(message);
        }
    }

    public bool TryDequeue(out Message message)
    {
        return _items.TryDequeue(out message!);
    }

    /// <summary>
    /// Waits until a message is handed to this receive - returned for it alone to deliver - or
    /// until <paramref name="cancellationToken"/> fires, which for a long poll means its
    /// <c>WaitTimeSeconds</c> elapsed, or the caller gave up; then this returns
    /// <see langword="null"/>. Cancellation is not an error here: the receive simply looks once
    /// more and returns whatever it has, so this returns rather than throwing.
    /// </summary>
    /// <remarks>
    /// A <see langword="null"/> return can also mean messages were already waiting, so parking
    /// would have slept through them; the caller drains the queue and, beaten to those messages
    /// by another consumer, can call this again to wait out the rest of its wait time - real
    /// SQS holds the call open rather than answering early.
    /// </remarks>
    public async Task<Message?> WaitForMessageAsync(CancellationToken cancellationToken)
    {
        if (!_items.IsEmpty || cancellationToken.IsCancellationRequested)
        {
            return null;
        }

        Waiter waiter;
        lock (SyncObj)
        {
            // Re-checked under the lock: a message enqueued since the check above went into
            // the queue only because nobody was parked to hand it to, so parking now would be
            // sleeping through it.
            if (!_items.IsEmpty)
            {
                return null;
            }

            waiter = new Waiter();
            Link(waiter);
        }

        try
        {
            using (cancellationToken.Register(static state => ((Waiter)state!).WakeEmpty(), waiter))
            {
                return await waiter.Handed.ConfigureAwait(true);
            }
        }
        finally
        {
            lock (SyncObj)
            {
                // No-op when an enqueue handed this receive a message, since the hand-off
                // already took it out of the list; this is for the receive that was cancelled
                // while still parked.
                Unlink(waiter);
            }
        }
    }

    /// <summary>A point-in-time view of the waiting messages, for the dashboard. Does not consume.</summary>
    public List<Message> Snapshot() => [.. _items];

    /// <summary>
    /// Removes a waiting message by id, for the dashboard's delete. <see langword="false"/> when
    /// no waiting message has that id - it may have been received already, in which case it is
    /// in flight rather than here.
    /// </summary>
    public bool TryRemove(string messageId)
    {
        var removed = false;
        lock (SyncObj)
        {
            // ConcurrentQueue has no remove, so rotate the queue: everything comes off and goes
            // back except the message being dropped. A concurrent receive can dequeue from under
            // this, which is fine - it is taking a message that was going to be delivered
            // anyway, and a dashboard delete racing a receive has no better answer. Bypassing
            // Enqueue for the re-add is also fine: these messages were already in _items, which
            // means nobody was parked when they arrived, and nobody can park mid-rotation
            // because parking takes this same lock.
            var count = _items.Count;
            for (var i = 0; i < count; i++)
            {
                if (!_items.TryDequeue(out var message))
                {
                    break;
                }

                if (!removed && string.Equals(message.MessageId, messageId, StringComparison.Ordinal))
                {
                    removed = true;
                    continue;
                }

                _items.Enqueue(message);
            }
        }

        return removed;
    }

    /// <summary>Discards every waiting message, for <c>PurgeQueue</c>.</summary>
    public void Clear()
    {
        lock (SyncObj)
        {
            while (_items.TryDequeue(out _))
            {
            }
        }
    }

    /// <summary>
    /// Removes and returns a parked receive picked uniformly at random, or <see langword="null"/>
    /// when none are parked. Must be called while holding <see cref="SyncObj"/>.
    /// </summary>
    /// <remarks>
    /// Random rather than round-robin (which taking the head of the list would give, since a
    /// receive re-parks at the head after each message). Real SQS spreads work across whoever is
    /// polling but does not deal it out in strict rotation, and an emulator that splits work
    /// perfectly evenly would let a test pass here that fails against SQS.
    /// </remarks>
    private Waiter? TryTakeRandomWaiter()
    {
        if (_waitersHead is null)
        {
            return null;
        }

        var index = NextRandom(_waiterCount);
        var waiter = _waitersHead;
        for (var i = 0; i < index; i++)
        {
            waiter = waiter!.Next;
        }

        Unlink(waiter!);
        return waiter;
    }

    /// <summary>Adds a receive at the head of the list. Must be called while holding <see cref="SyncObj"/>.</summary>
    private void Link(Waiter waiter)
    {
        waiter.Next = _waitersHead;
        waiter.IsLinked = true;
        _waitersHead = waiter;
        _waiterCount++;
    }

    /// <summary>
    /// Removes a receive from the list, if it is still in it. Must be called while holding
    /// <see cref="SyncObj"/>. The list holds one entry per in-flight long poll on this queue, so
    /// walking it is cheap.
    /// </summary>
    private void Unlink(Waiter waiter)
    {
        if (!waiter.IsLinked)
        {
            return;
        }

        if (ReferenceEquals(_waitersHead, waiter))
        {
            _waitersHead = waiter.Next;
        }
        else
        {
            var previous = _waitersHead;
            while (previous is not null && !ReferenceEquals(previous.Next, waiter))
            {
                previous = previous.Next;
            }

            if (previous is null)
            {
                return;
            }

            previous.Next = waiter.Next;
        }

        waiter.Next = null;
        waiter.IsLinked = false;
        _waiterCount--;
    }

#pragma warning disable CA5394 // Which consumer gets a message is load spreading, not a security decision.
    private static int NextRandom(int exclusiveUpperBound)
    {
#if NET
        return Random.Shared.Next(exclusiveUpperBound);
#else
        lock (SharedRandom)
        {
            return SharedRandom.Next(exclusiveUpperBound);
        }
#endif
    }
#pragma warning restore CA5394

    /// <summary>
    /// One receive parked on this store. Doubles as its own list node, so parking costs no more
    /// than the waiter itself. <see cref="Next"/> and <see cref="IsLinked"/> are only touched
    /// under <see cref="SyncObj"/>; completing the task is safe from anywhere, and first
    /// completion wins - a message handed over, or <see langword="null"/> from cancellation.
    /// </summary>
    private sealed class Waiter
    {
        private readonly TaskCompletionSource<Message?> _handed = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Waiter? Next;
        public bool IsLinked;

        public Task<Message?> Handed => _handed.Task;

        /// <summary>
        /// Gives this receive the message, which is now its alone to deliver.
        /// <see langword="false"/> when the receive was already cancelled, in which case the
        /// message is still the caller's to place.
        /// </summary>
        public bool TryHand(Message message) => _handed.TrySetResult(message);

        /// <summary>Wakes this receive with nothing, on cancellation.</summary>
        public void WakeEmpty() => _handed.TrySetResult(null);
    }
}
