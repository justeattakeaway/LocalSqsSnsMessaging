// Adapted from UnboundedChannel<T> in dotnet/runtime
// (src/libraries/System.Threading.Channels/src/System/Threading/Channels/UnboundedChannel.cs),
// which is licensed to the .NET Foundation under the MIT license.
//
// The structure is the original's: a ConcurrentQueue holding the items, a lock taken on that
// queue guarding an intrusive linked list of parked readers, and writers handing a wake-up to a
// reader from inside that lock. What changed is who gets woken, and what they are woken to do:
//
//   - UnboundedChannel has two reader lists. ReadAsync parks on the blocked-reader list and a
//     write hands the item to exactly one of them; WaitToReadAsync parks on the waiting-reader
//     list and a write wakes every one of them to race for the item. A receive wants up to
//     MaxNumberOfMessages messages and may reject one it pulls (routing it to a dead-letter
//     queue instead), so it cannot use ReadAsync's take-exactly-one hand-off - it has to be on
//     the broadcast list, where the winner of the race is decided by continuation scheduling.
//     Over HTTP that order can settle, and one consumer takes every message. Here there is a
//     single list, and a write wakes exactly one parked receive, chosen at random.
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
/// Enqueuing is the only way in, and it always wakes a parked receive, so no delivery path can
/// add a message that waiting consumers sleep through.
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
    /// Adds a message and wakes one parked receive, chosen at random.
    /// </summary>
    public void Enqueue(Message message)
    {
        Waiter? woken;
        lock (SyncObj)
        {
            _items.Enqueue(message);

            // Taken out of the list here, under the same lock that added the message, so the
            // wake-up belongs to this receive alone and no other consumer races it for the
            // message. If that receive gives up before taking it, Depart hands the wake-up on.
            woken = TryTakeRandomWaiter();
        }

        // Outside the lock: waking a receive runs its continuation, which comes straight back
        // into this store to drain the message.
        woken?.Wake();
    }

    public bool TryDequeue(out Message message)
    {
        return _items.TryDequeue(out message!);
    }

    /// <summary>
    /// Waits until a message may be available for this receive to take, or until
    /// <paramref name="cancellationToken"/> fires - which for a long poll means its
    /// <c>WaitTimeSeconds</c> elapsed, or the caller gave up. Cancellation is not an error here:
    /// the receive simply looks once more and returns whatever it has, so this returns rather
    /// than throwing.
    /// </summary>
    /// <remarks>
    /// A receive that wakes to find another consumer got there first can call this again to wait
    /// out the rest of its wait time; real SQS holds the call open rather than answering early.
    /// </remarks>
    public async Task WaitForMessageAsync(CancellationToken cancellationToken)
    {
        if (!_items.IsEmpty || cancellationToken.IsCancellationRequested)
        {
            return;
        }

        Waiter waiter;
        lock (SyncObj)
        {
            // Re-checked under the lock: a message enqueued since the check above has already
            // handed its wake-up to someone, so parking now would be sleeping through it.
            if (!_items.IsEmpty)
            {
                return;
            }

            waiter = new Waiter();
            Link(waiter);
        }

        try
        {
            using (cancellationToken.Register(static state => ((Waiter)state!).Wake(), waiter))
            {
                await waiter.Woken.ConfigureAwait(true);
            }
        }
        finally
        {
            Depart(waiter);
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
            // anyway, and a dashboard delete racing a receive has no better answer.
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

        // The rotation put messages back without going through Enqueue, so nothing was woken for
        // them; a receive that parked while the queue was mid-rotation needs telling.
        WakeOneIfMessagesWaiting();
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
    /// Called when a receive stops waiting. If it was the one a message was handed to but it
    /// timed out (or was cancelled) before taking it, that wake-up died with it, so pass one on
    /// to another parked receive rather than leave the message sitting behind consumers that are
    /// still waiting.
    /// </summary>
    private void Depart(Waiter waiter)
    {
        lock (SyncObj)
        {
            Unlink(waiter);
        }

        WakeOneIfMessagesWaiting();
    }

    private void WakeOneIfMessagesWaiting()
    {
        Waiter? woken = null;
        lock (SyncObj)
        {
            if (!_items.IsEmpty)
            {
                woken = TryTakeRandomWaiter();
            }
        }

        woken?.Wake();
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
    /// under <see cref="SyncObj"/>; <see cref="Wake"/> is safe to call from anywhere.
    /// </summary>
    private sealed class Waiter
    {
        private readonly TaskCompletionSource<bool> _woken = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Waiter? Next;
        public bool IsLinked;

        public Task Woken => _woken.Task;

        public void Wake() => _woken.TrySetResult(true);
    }
}
