using System.Collections.Concurrent;
using System.Threading.Channels;
using LocalSqsSnsMessaging.Sqs.Model;

namespace LocalSqsSnsMessaging;

// Receiving: taking visible messages into flight, long polling, redrive to the dead-letter queue at
// maxReceiveCount, and FIFO receive attempts.
internal sealed partial class SqsQueueResource
{
    /// <summary>What a receive asks of the queue, after the request's defaults are applied.</summary>
    /// <param name="MaxMessages">The most messages to return.</param>
    /// <param name="VisibilityTimeout">How long received messages stay in flight; zero makes them visible again as the receive returns.</param>
    /// <param name="WaitTime">How long to wait for a message when none is visible; zero for a short poll.</param>
    /// <param name="AttemptId">A FIFO receive's <c>ReceiveRequestAttemptId</c>: a retry under the same id gets the same batch back.</param>
    internal readonly record struct ReceiveRequest(int MaxMessages, TimeSpan VisibilityTimeout, TimeSpan WaitTime, string? AttemptId);

    /// <summary>
    /// Receives up to <see cref="ReceiveRequest.MaxMessages"/> visible messages, waiting up to
    /// <see cref="ReceiveRequest.WaitTime"/> for one, and puts them in flight. A standard queue hands
    /// each message to one waiting receive; a FIFO queue delivers from a message group only while
    /// none of its messages is in flight, in sequence order, and replays the batch of an earlier
    /// receive made under the same attempt id.
    /// </summary>
    /// <remarks>
    /// On the way, a message past the retention period is discarded, and one received
    /// <see cref="MaxReceiveCount"/> times already is redriven to the dead-letter queue (when it
    /// resolves and its <c>RedriveAllowPolicy</c> admits this queue) instead of delivered.
    /// </remarks>
    /// <returns>
    /// A copy of each message, with every attribute and its new receipt handle, for the caller to
    /// trim to what the request asked for. Changing it does not change the queue's message.
    /// </returns>
    /// <exception cref="AwsServiceException">
    /// The attempt id names a receive one of whose messages has since been deleted or had its
    /// visibility changed, so it can no longer be retried.
    /// </exception>
    public async Task<List<Message>> ReceiveAsync(ReceiveRequest receive, CancellationToken cancellationToken)
    {
        var batch = IsFifo
            ? await ReceiveFifoAsync(receive, cancellationToken).ConfigureAwait(true)
            : await ReceiveStandardAsync(receive, cancellationToken).ConfigureAwait(true);

        // Visibility timeouts start as the receive returns, and a zero one ends then, as on SQS: a
        // timer running mid-receive could make a message visible again while the receive is still
        // taking messages, handing it out twice in one batch.
        foreach (var message in batch)
        {
            StartVisibilityTimeout(message.ReceiptHandle!, receive.VisibilityTimeout);
        }

        return batch;
    }

    private async Task<List<Message>> ReceiveStandardAsync(ReceiveRequest receive, CancellationToken cancellationToken)
    {
        var batch = new List<Message>();
        TakeWaiting();
        if (batch.Count > 0 || receive.WaitTime == TimeSpan.Zero)
        {
            return batch;
        }

        using var receiveTimeout = TimeProvider.CreateCancellationTokenSource(receive.WaitTime);
        var waitDeadline = TimeProvider.GetUtcNow() + receive.WaitTime;
        using var linkedToken = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, receiveTimeout.Token);

        // Keep waiting until this receive is handed a message or its wait time runs out. Being
        // beaten to the waiting messages by another consumer is not the end of the poll: real SQS
        // holds the call open for the rest of WaitTimeSeconds rather than answering early.
        while (true)
        {
            TakeWaiting();
            if (batch.Count > 0 || linkedToken.IsCancellationRequested)
            {
                return batch;
            }

            Message? handedMessage;
            using (Bus.EnterWaitingReceive(waitDeadline))
            {
                handedMessage = await _waiting.WaitForMessageAsync(linkedToken.Token).ConfigureAwait(true);
            }

            if (handedMessage is not null)
            {
                // Handed to this receive alone. If it was due for the dead-letter queue instead,
                // the next time round goes back to waiting.
                TryDeliver(handedMessage, TimeProvider.GetUtcNow(), batch, groupId: null, group: null);
            }
        }

        void TakeWaiting()
        {
            var now = TimeProvider.GetUtcNow();
            while (batch.Count < receive.MaxMessages && _waiting.TryDequeue(out var message))
            {
                TryDeliver(message, now, batch, groupId: null, group: null);
            }
        }
    }

    /// <summary>
    /// Wake-up signal for FIFO long polling. FIFO messages live in per-group queues, so a waiting
    /// receive has no single store to park on; instead it waits on this channel, which is pulsed
    /// whenever a message may have become deliverable (enqueue, redelivery, or a group unlocking).
    /// Unlike a standard queue, which hands a message to one waiting receive, this wakes every
    /// waiter: whether a given receive can take anything depends on which groups are locked, so
    /// they all re-check. Bounded at 1 with DropWrite, since one pending pulse wakes everyone.
    /// </summary>
    private readonly Channel<bool> _fifoMessageAvailable = Channel.CreateBounded<bool>(
        new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });

    private void SignalFifoMessageAvailable() => _fifoMessageAvailable.Writer.TryWrite(true);

    private int _fifoReceiveStart;

    private async Task<List<Message>> ReceiveFifoAsync(ReceiveRequest receive, CancellationToken cancellationToken)
    {
        if (receive.AttemptId is { } attemptId && TryReplayReceiveAttempt(attemptId) is { } replayed)
        {
            return replayed;
        }

        var batch = TakeFromGroups(receive);
        if (batch.Count == 0 && receive.WaitTime > TimeSpan.Zero)
        {
            // The timeout is TimeProvider-driven so FakeTimeProvider-based tests still work.
            using var receiveTimeout = TimeProvider.CreateCancellationTokenSource(receive.WaitTime);
            var waitDeadline = TimeProvider.GetUtcNow() + receive.WaitTime;
            using var linkedToken = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, receiveTimeout.Token);
            var signalReader = _fifoMessageAvailable.Reader;

            while (batch.Count == 0)
            {
                try
                {
                    using (Bus.EnterWaitingReceive(waitDeadline))
                    {
                        await signalReader.WaitToReadAsync(linkedToken.Token).ConfigureAwait(true);
                    }
                }
                catch (OperationCanceledException)
                {
                    // Wait time elapsed or the caller cancelled: one final look below.
                }

                // Consume the pending pulse (every waiter re-checks the groups, so a lost race
                // here just means another waiter got the message).
                while (signalReader.TryRead(out _))
                {
                }

                batch = TakeFromGroups(receive);
                if (linkedToken.IsCancellationRequested)
                {
                    break;
                }
            }
        }

        if (receive.AttemptId is { } newAttemptId && batch.Count > 0)
        {
            RecordReceiveAttempt(newAttemptId, batch);
        }

        return batch;
    }

    private List<Message> TakeFromGroups(ReceiveRequest receive)
    {
        var batch = new List<Message>();

        // Read before any group is locked: a clock that moves when read can run timers from inside
        // the read, and those take group locks.
        var now = TimeProvider.GetUtcNow();

        // Start each receive at the next group in turn: always scanning from the same group would
        // let one group that keeps being released (a visibility timeout of 0) starve the rest.
        var groups = _groups.ToArray();
        var start = groups.Length == 0 ? 0 : (int)((uint)Interlocked.Increment(ref _fifoReceiveStart) % (uint)groups.Length);
        for (var i = 0; i < groups.Length && batch.Count < receive.MaxMessages; i++)
        {
            var (groupId, group) = groups[(start + i) % groups.Length];
            TryInExistingGroup(groupId, group, held =>
            {
                // A group with a message in flight is locked: SQS delivers nothing further from it
                // until that message is deleted or visible again. The messages this receive takes
                // from the group itself come out in order, so it keeps going.
                if (held.Held > 0)
                {
                    return false;
                }

                while (batch.Count < receive.MaxMessages && held.Waiting.First is { } head)
                {
                    held.Waiting.RemoveFirst();
                    TryDeliver(head.Value, now, batch, groupId, held);
                }

                return true;
            });
        }

        return batch;
    }

    /// <summary>
    /// Delivers a message just taken from the waiting messages: into flight and onto
    /// <paramref name="batch"/>, unless it is past the retention period (discarded) or at its
    /// <see cref="MaxReceiveCount"/> (redriven). On a FIFO queue the caller holds <paramref name="group"/>'s lock.
    /// </summary>
    /// <remarks>The message's visibility timeout is not started: <see cref="ReceiveAsync"/> starts it once the batch is complete.</remarks>
    private void TryDeliver(Message message, DateTimeOffset now, List<Message> batch, string? groupId, FifoGroup? group)
    {
        if (IsExpired(message, now))
        {
            Forget(message);
            return;
        }

        // A dead-letter queue deleted since the policy was set leaves the message where it is.
        if (IsAtMaxReceiveCount(message) && Bus.TryGetQueueByArn(DeadLetterTargetArn, out var deadLetterQueue)
            && SqsQueueValidation.RedriveAllowed(deadLetterQueue, Arn))
        {
            message.Attributes ??= [];
            message.Attributes[InternalMessageSystemAttributeName.DeadLetterQueueSourceArn] = Arn;
            if (deadLetterQueue.IsFifo)
            {
                // In a FIFO dead-letter queue the message is deduplicated by its original id (and
                // its retention period starts over, which AcceptRedriven sees to).
                message.Attributes[InternalMessageSystemAttributeName.MessageDeduplicationId] = message.MessageId!;
            }

            Forget(message);
            deadLetterQueue.AcceptRedriven(message, now);
            return;
        }

        message.Attributes ??= [];
        var receiveCount = int.Parse(message.Attributes.GetValueOrDefault(InternalMessageSystemAttributeName.ApproximateReceiveCount) ?? "0",
            NumberFormatInfo.InvariantInfo) + 1;
        message.Attributes[InternalMessageSystemAttributeName.ApproximateReceiveCount] = receiveCount.ToString(NumberFormatInfo.InvariantInfo);

        // Set the first time a message is delivered, and stable on later receives.
        if (!message.Attributes.ContainsKey(InternalMessageSystemAttributeName.ApproximateFirstReceiveTimestamp))
        {
            message.Attributes[InternalMessageSystemAttributeName.ApproximateFirstReceiveTimestamp] =
                now.ToUnixTimeMilliseconds().ToString(NumberFormatInfo.InvariantInfo);
        }

        var receiptHandle = SqsReceiptHandle.Create(this, message.MessageId!, now);
        var received = CopyOf(message);
        received.ReceiptHandle = receiptHandle;
        batch.Add(received);
        EnterFlight(message, receiptHandle, Timeout.InfiniteTimeSpan, groupId, group);
    }

    private bool IsAtMaxReceiveCount(Message message)
    {
        var receiveCount = message.Attributes?.GetValueOrDefault(InternalMessageSystemAttributeName.ApproximateReceiveCount) ?? "0";
        return MaxReceiveCount is not null && int.Parse(receiveCount, NumberFormatInfo.InvariantInfo) >= MaxReceiveCount;
    }

    /// <summary>
    /// A FIFO receive made under a <c>ReceiveRequestAttemptId</c>: the batch it returned, which a
    /// retry with the same id replays until one of its messages is deleted or has its visibility
    /// changed.
    /// </summary>
    private sealed class ReceiveAttempt(List<Message> batch, DateTimeOffset receivedAt)
    {
        public List<Message> Batch { get; } = batch;
        public DateTimeOffset ReceivedAt { get; } = receivedAt;
        public bool Modified { get; set; }
    }

    /// <summary>SQS honours a <c>ReceiveRequestAttemptId</c> for five minutes.</summary>
    private static readonly TimeSpan ReceiveAttemptLifetime = TimeSpan.FromMinutes(5);

    /// <summary>FIFO receive attempts by <c>ReceiveRequestAttemptId</c>.</summary>
    private readonly ConcurrentDictionary<string, ReceiveAttempt> _receiveAttempts = new(StringComparer.Ordinal);

    /// <summary>The receive attempt that handed out each receipt handle, for the handles of attempts still held.</summary>
    private readonly ConcurrentDictionary<string, ReceiveAttempt> _receiveAttemptsByReceiptHandle = new(StringComparer.Ordinal);

    /// <summary>Marks the receive attempt that handed out <paramref name="receiptHandle"/>, if any, as no longer retryable.</summary>
    private void MarkReceiveAttemptModified(string receiptHandle)
    {
        if (_receiveAttemptsByReceiptHandle.TryGetValue(receiptHandle, out var attempt))
        {
            attempt.Modified = true;
        }
    }

    private void RecordReceiveAttempt(string attemptId, List<Message> batch)
    {
        var now = TimeProvider.GetUtcNow();
        foreach (var (expiredId, expired) in _receiveAttempts)
        {
            if (now - expired.ReceivedAt >= ReceiveAttemptLifetime)
            {
                ForgetReceiveAttempt(expiredId, expired);
            }
        }

        var attempt = new ReceiveAttempt(batch.ConvertAll(CopyOf), now);
        _receiveAttempts[attemptId] = attempt;
        foreach (var message in batch)
        {
            _receiveAttemptsByReceiptHandle[message.ReceiptHandle!] = attempt;
        }
    }

    /// <summary>
    /// Replays the batch an earlier FIFO receive returned under <paramref name="attemptId"/>, with the
    /// same receipt handles, restarting each message's visibility timeout.
    /// </summary>
    /// <returns>
    /// The replayed batch, or <see langword="null"/> when there is nothing to replay (an unknown or
    /// expired id, or a batch whose messages are no longer in flight), and the receive goes ahead
    /// as a new one.
    /// </returns>
    private List<Message>? TryReplayReceiveAttempt(string attemptId)
    {
        if (!_receiveAttempts.TryGetValue(attemptId, out var attempt))
        {
            return null;
        }

        if (TimeProvider.GetUtcNow() - attempt.ReceivedAt >= ReceiveAttemptLifetime)
        {
            ForgetReceiveAttempt(attemptId, attempt);
            return null;
        }

        if (attempt.Modified)
        {
            throw SqsRequestValidation.InvalidParameterValue("ReceiveRequestAttemptId", attemptId,
                "The receiveRequestAttemptId is not valid anymore since the receive call is not retryable.");
        }

        foreach (var message in attempt.Batch)
        {
            if (!_inFlight.ContainsKey(message.ReceiptHandle!))
            {
                // Its visibility timeout ran out: the messages are back on the queue.
                ForgetReceiveAttempt(attemptId, attempt);
                return null;
            }
        }

        // ReceiveAsync restarts each message's visibility timeout as it returns.
        return attempt.Batch.ConvertAll(CopyOf);
    }

    private void ForgetReceiveAttempt(string attemptId, ReceiveAttempt attempt)
    {
        // Removes the entry only if it is still this attempt (atomic on a ConcurrentDictionary).
        ((ICollection<KeyValuePair<string, ReceiveAttempt>>)_receiveAttempts)
            .Remove(new KeyValuePair<string, ReceiveAttempt>(attemptId, attempt));
        foreach (var message in attempt.Batch)
        {
            _receiveAttemptsByReceiptHandle.TryRemove(message.ReceiptHandle!, out _);
        }
    }
}
