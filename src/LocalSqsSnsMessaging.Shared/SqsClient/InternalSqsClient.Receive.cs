#pragma warning disable CS8600, CS8601, CS8602, CS8604, CS8619 // Nullable reference warnings - internal POCOs use nullable properties but values are set at runtime

using System.Collections.Concurrent;
using LocalSqsSnsMessaging.Sqs.Model;

namespace LocalSqsSnsMessaging;

/// <summary>ReceiveMessage: standard and FIFO receives, receive-attempt replay and attribute filtering.</summary>
internal sealed partial class InternalSqsClient
{
    public async Task<ReceiveMessageResponse> ReceiveMessageAsync(ReceiveMessageRequest request,
        CancellationToken cancellationToken = default)
    {
        var (response, queue, visibilityTimeout) = await ReceiveMessageCoreAsync(request, cancellationToken).ConfigureAwait(true);

        // Messages received with a zero visibility timeout are visible again as soon as the
        // receive returns, as on SQS. The timeout is the one the receive applied: the queue's may
        // have changed during a long poll.
        if (response.Messages is { Count: > 0 } received && visibilityTimeout == TimeSpan.Zero)
        {
            foreach (var message in received)
            {
                SqsInflightMessageExpirationJob.EndVisibilityTimeout(queue, message.ReceiptHandle!, _bus.TimeProvider);
            }
        }

        return response;
    }

    /// <returns>The response, the queue it was served from, and the visibility timeout given to its messages.</returns>
    private async Task<(ReceiveMessageResponse Response, SqsQueueResource Queue, TimeSpan VisibilityTimeout)> ReceiveMessageCoreAsync(
        ReceiveMessageRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var queueName = GetQueueNameFromUrl(request.QueueUrl!);
        if (!Queues.TryGetValue(queueName, out var queue))
        {
            throw new InternalQueueDoesNotExistException();
        }
        Authorize(queue, SqsActionName.ReceiveMessage);

        SqsRequestValidation.ValidateReceive(request);

        List<Message>? messages = null;
        // A receive that names no wait time or visibility timeout takes the queue's; an explicit
        // zero is honoured (a short poll, or a message that is visible again immediately).
        var waitTime = request.WaitTimeSeconds is { } requestWaitTime
            ? TimeSpan.FromSeconds(requestWaitTime)
            : queue.ReceiveMessageWaitTime;
        var visibilityTimeout = request.VisibilityTimeout is { } requestVisibilityTimeout
            ? TimeSpan.FromSeconds(requestVisibilityTimeout)
            : queue.VisibilityTimeout;
        var requestedSystemAttributes = GetRequestedSystemAttributes(request);
        var requestedMessageAttributes = request.MessageAttributeNames;

        cancellationToken.ThrowIfCancellationRequested();

        if (!queue.IsFifo)
        {
            ReadAvailableMessages();
            if (messages is not null && messages.Count > 0 || waitTime == TimeSpan.Zero)
            {
                _bus.RecordOperation(AwsServiceName.Sqs, SqsActionName.ReceiveMessage, queue.Arn);
                return (new ReceiveMessageResponse
                {
                    Messages = messages.ToInitializedList()
                }.SetCommonProperties(), queue, visibilityTimeout);
            }

            using var receiveTimeout = _bus.TimeProvider.CreateCancellationTokenSource(waitTime);
            var waitDeadline = _bus.TimeProvider.GetUtcNow() + waitTime;
            using var linkedToken =
                CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, receiveTimeout.Token);

            // Keep waiting until this receive is handed a message or its wait time runs out.
            // Being beaten to the waiting messages by another consumer is not the end of the
            // poll: real SQS holds the call open for the rest of WaitTimeSeconds rather than
            // answering early, so we go round again.
            while (true)
            {
                ReadAvailableMessages();

                if (messages is { Count: > 0 } || linkedToken.IsCancellationRequested)
                {
                    break;
                }

                Message? handedMessage;
                using (_bus.EnterWaitingReceive(waitDeadline))
                {
                    handedMessage = await queue.Messages.WaitForMessageAsync(linkedToken.Token).ConfigureAwait(true);
                }

                if (handedMessage is not null)
                {
                    // Handed to this receive alone; no other consumer raced us for it. If it
                    // was due for the dead-letter queue instead of delivery, the next loop
                    // iteration goes back to waiting.
                    ReceiveMessageImpl(handedMessage, ref messages, queue, visibilityTimeout, requestedSystemAttributes, requestedMessageAttributes);
                }
            }
        }
        else
        {
            if (request.ReceiveRequestAttemptId is { } attemptId && TryReplayReceiveAttempt(queue, attemptId, visibilityTimeout) is { } replayed)
            {
                _bus.RecordOperation(AwsServiceName.Sqs, SqsActionName.ReceiveMessage, queue.Arn);
                return (new ReceiveMessageResponse { Messages = replayed }.SetCommonProperties(), queue, visibilityTimeout);
            }

            messages = ReceiveFifoMessages(queue, request.MaxNumberOfMessages ?? 1, visibilityTimeout, requestedSystemAttributes, requestedMessageAttributes);

            if ((messages is null || messages.Count == 0) && waitTime > TimeSpan.Zero)
            {
                // FIFO long polling. FIFO messages live in per-group queues rather than the
                // channel, so there is no reader to await; instead wait on the queue's FIFO
                // signal, which is pulsed whenever a message may have become deliverable
                // (enqueue, visibility expiry, or a delete unlocking a message group). The
                // timeout is TimeProvider-driven so FakeTimeProvider-based tests still work.
                using var receiveTimeout = _bus.TimeProvider.CreateCancellationTokenSource(waitTime);
                var waitDeadline = _bus.TimeProvider.GetUtcNow() + waitTime;
                using var linkedToken =
                    CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, receiveTimeout.Token);
                var signalReader = queue.FifoMessageAvailableSignal.Reader;

                while (messages is null || messages.Count == 0)
                {
                    try
                    {
                        using (_bus.EnterWaitingReceive(waitDeadline))
                        {
                            await signalReader.WaitToReadAsync(linkedToken.Token).ConfigureAwait(true);
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        // Wait time elapsed or the caller cancelled; fall through to one
                        // final check below, mirroring the standard-queue behaviour.
                    }

                    // Consume the pending pulse (every waiter re-checks the group queues,
                    // so a lost race here just means another waiter got the message).
                    while (signalReader.TryRead(out _))
                    {
                    }

                    messages = ReceiveFifoMessages(queue, request.MaxNumberOfMessages ?? 1, visibilityTimeout, requestedSystemAttributes, requestedMessageAttributes);

                    if (linkedToken.IsCancellationRequested)
                    {
                        break;
                    }
                }
            }

            if (request.ReceiveRequestAttemptId is { } newAttemptId && messages is { Count: > 0 })
            {
                ForgetExpiredReceiveAttempts(queue);
                var attempt = new SqsQueueResource.ReceiveAttempt([.. messages.Select(CloneMessage)], _bus.TimeProvider.GetUtcNow());
                queue.ReceiveAttempts[newAttemptId] = attempt;
                foreach (var message in messages)
                {
                    queue.ReceiveAttemptsByReceiptHandle[message.ReceiptHandle!] = attempt;
                }
            }
        }

        _bus.RecordOperation(AwsServiceName.Sqs, SqsActionName.ReceiveMessage, queue.Arn);
        return (new ReceiveMessageResponse
        {
            Messages = messages.ToInitializedList(),
        }.SetCommonProperties(), queue, visibilityTimeout);

        void ReadAvailableMessages()
        {
            while (queue.Messages.TryDequeue(out var message))
            {
                ReceiveMessageImpl(message, ref messages, queue, visibilityTimeout, requestedSystemAttributes, requestedMessageAttributes);
                if (messages is not null && messages.Count >= (request.MaxNumberOfMessages ?? 1))
                {
                    break;
                }
            }
        }
    }

    /// <summary>SQS honours a <c>ReceiveRequestAttemptId</c> for five minutes.</summary>
    private static readonly TimeSpan ReceiveAttemptLifetime = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Replays the batch an earlier FIFO receive returned under <paramref name="attemptId"/>, with the
    /// same receipt handles, restarting each message's visibility timeout.
    /// </summary>
    /// <returns>
    /// The replayed batch, or <see langword="null"/> when there is nothing to replay (an unknown or
    /// expired id, or a batch whose messages are no longer in flight), and the receive goes ahead
    /// as a new one.
    /// </returns>
    /// <exception cref="AwsServiceException">One of the batch's messages was deleted or had its visibility changed.</exception>
    private List<Message>? TryReplayReceiveAttempt(SqsQueueResource queue, string attemptId, TimeSpan visibilityTimeout)
    {
        if (!queue.ReceiveAttempts.TryGetValue(attemptId, out var attempt))
        {
            return null;
        }

        if (_bus.TimeProvider.GetUtcNow() - attempt.ReceivedAt >= ReceiveAttemptLifetime)
        {
            ForgetReceiveAttempt(queue, attemptId, attempt);
            return null;
        }

        if (attempt.Modified)
        {
            throw SqsRequestValidation.InvalidParameterValue("ReceiveRequestAttemptId", attemptId,
                "The receiveRequestAttemptId is not valid anymore since the receive call is not retryable.");
        }

        var inFlight = new List<SqsInflightMessageExpirationJob>(attempt.Batch.Count);
        foreach (var message in attempt.Batch)
        {
            if (!queue.InFlightMessages.TryGetValue(message.ReceiptHandle!, out var entry))
            {
                // Its visibility timeout ran out: the messages are back on the queue.
                ForgetReceiveAttempt(queue, attemptId, attempt);
                return null;
            }

            inFlight.Add(entry.Item2);
        }

        foreach (var expirationJob in inFlight)
        {
            expirationJob.UpdateTimeout(visibilityTimeout);
        }

        return [.. attempt.Batch.Select(CloneMessage)];
    }

    private void ForgetExpiredReceiveAttempts(SqsQueueResource queue)
    {
        var now = _bus.TimeProvider.GetUtcNow();
        foreach (var (attemptId, attempt) in queue.ReceiveAttempts)
        {
            if (now - attempt.ReceivedAt >= ReceiveAttemptLifetime)
            {
                ForgetReceiveAttempt(queue, attemptId, attempt);
            }
        }
    }

    private static void ForgetReceiveAttempt(SqsQueueResource queue, string attemptId, SqsQueueResource.ReceiveAttempt attempt)
    {
        // Removes the entry only if it is still this attempt (atomic on a ConcurrentDictionary).
        ((ICollection<KeyValuePair<string, SqsQueueResource.ReceiveAttempt>>)queue.ReceiveAttempts)
            .Remove(new KeyValuePair<string, SqsQueueResource.ReceiveAttempt>(attemptId, attempt));
        foreach (var message in attempt.Batch)
        {
            queue.ReceiveAttemptsByReceiptHandle.TryRemove(message.ReceiptHandle!, out _);
        }
    }

    /// <summary>
    /// The system attributes a receive asked for: <c>MessageSystemAttributeNames</c> together with
    /// the deprecated <c>AttributeNames</c>, which SQS still honours.
    /// </summary>
    private static List<string>? GetRequestedSystemAttributes(ReceiveMessageRequest request)
    {
        if (request.AttributeNames is not { Count: > 0 } legacyNames)
        {
            return request.MessageSystemAttributeNames;
        }

        if (request.MessageSystemAttributeNames is not { Count: > 0 } systemNames)
        {
            return legacyNames;
        }

        return [.. systemNames.Union(legacyNames, StringComparer.Ordinal)];
    }

    /// <returns>
    /// <see langword="true"/> when the message was delivered to the caller (and is now
    /// in flight); <see langword="false"/> when it was routed to the dead-letter queue.
    /// </returns>
    private bool ReceiveMessageImpl(Message message, ref List<Message>? messages, SqsQueueResource queue, TimeSpan visibilityTimeout, List<string>? requestedSystemAttributes, List<string>? requestedMessageAttributes)
    {
        if (queue.IsExpired(message, _bus.TimeProvider.GetUtcNow()))
        {
            // Past the queue's retention period: discarded instead of delivered.
            queue.LatestReceiptHandles.TryRemove(message.MessageId!, out _);
            return false;
        }

        // A dead-letter queue deleted since the policy was set leaves the message where it is.
        if (IsAtMaxReceiveCount(message, queue) && _bus.TryGetQueueByArn(queue.DeadLetterTargetArn, out var deadLetterQueue)
            && SqsQueueValidation.RedriveAllowed(deadLetterQueue, queue.Arn))
        {
            message.Attributes ??= [];
            message.Attributes[InternalMessageSystemAttributeName.DeadLetterQueueSourceArn] = queue.Arn;
            if (deadLetterQueue.IsFifo)
            {
                // In a FIFO dead-letter queue the message is deduplicated by its original id (and
                // its retention period starts over, which AcceptRedriven sees to).
                message.Attributes[InternalMessageSystemAttributeName.MessageDeduplicationId] = message.MessageId!;
            }

            queue.LatestReceiptHandles.TryRemove(message.MessageId!, out _);
            deadLetterQueue.AcceptRedriven(message, _bus.TimeProvider.GetUtcNow());
            return false;
        }
        IncrementReceiveCount(message);

        // Real AWS sets ApproximateFirstReceiveTimestamp the first time a message is
        // delivered, and leaves it stable on subsequent receives. We piggy-back on
        // ApproximateReceiveCount: by this point IncrementReceiveCount has already
        // bumped the counter, so a value of "1" means this is the first receive.
        message.Attributes ??= [];
        if (!message.Attributes.ContainsKey(InternalMessageSystemAttributeName.ApproximateFirstReceiveTimestamp))
        {
            message.Attributes[InternalMessageSystemAttributeName.ApproximateFirstReceiveTimestamp] =
                _bus.TimeProvider.GetUtcNow().ToUnixTimeMilliseconds().ToString(NumberFormatInfo.InvariantInfo);
        }

        var clonedMessage = CloneMessage(message);
        FilterSystemAttributes(clonedMessage, requestedSystemAttributes);
        FilterMessageAttributes(clonedMessage, requestedMessageAttributes);
        var receiptHandle = CreateReceiptHandle(message, queue);
        clonedMessage.ReceiptHandle = receiptHandle;
        messages ??= [];
        messages.Add(clonedMessage);

        // Register the message as in flight, and take its FIFO group's in-flight slot, before its
        // visibility timer can fire: with a zero timeout the timer can run straight away, and must
        // find the message to return it and the slot to release.
        var expirationJob = new SqsInflightMessageExpirationJob(receiptHandle, queue, Timeout.InfiniteTimeSpan, _bus.TimeProvider);
        queue.LatestReceiptHandles[message.MessageId!] = receiptHandle;
        queue.InFlightMessages[receiptHandle] = (message, expirationJob);
        if (queue.IsFifo && message.Attributes.TryGetValue(InternalMessageSystemAttributeName.MessageGroupId, out var groupId))
        {
            queue.InFlightGroupCounts.AddOrUpdate(groupId, 1, (_, count) => count + 1);
        }

        // A zero visibility timeout ends when the receive returns (ReceiveMessageAsync), not on a
        // timer: a timer could make the message visible again mid-receive, handing it out twice in
        // one batch, or (on the real clock) only some time after the call has returned.
        if (visibilityTimeout > TimeSpan.Zero)
        {
            expirationJob.UpdateTimeout(visibilityTimeout);
        }
        return true;
    }

    private List<Message>? ReceiveFifoMessages(SqsQueueResource queue, int maxMessages, TimeSpan visibilityTimeout, List<string>? requestedSystemAttributes, List<string>? requestedMessageAttributes)
    {
        List<Message>? messages = null;

        // Start each receive at the next group in turn: always scanning from the same group would
        // let one group that keeps being released (a visibility timeout of 0) starve the rest.
        var groupIds = queue.MessageGroups.Keys.ToArray();
        var start = groupIds.Length == 0 ? 0 : (int)((uint)queue.NextFifoReceiveStart() % (uint)groupIds.Length);
        for (var i = 0; i < groupIds.Length; i++)
        {
            if (messages is not null && messages.Count >= maxMessages)
            {
                break;
            }

            var groupId = groupIds[(start + i) % groupIds.Length];
            var groupLock = queue.MessageGroupLocks.GetOrAdd(groupId, _ => new object());

            lock (groupLock)
            {
                // A group with an in-flight (received-but-not-deleted) message is locked:
                // real SQS FIFO delivers nothing further from the group until that message
                // is deleted or its visibility timeout expires, preserving ordering when
                // consumers process messages concurrently across groups.
                if (queue.InFlightGroupCounts.TryGetValue(groupId, out var inFlightCount) && inFlightCount > 0)
                {
                    continue;
                }

                // Re-fetch under the lock: visibility-timeout redelivery swaps in a rebuilt
                // queue instance, so the enumerator's snapshot may be stale.
                if (!queue.MessageGroups.TryGetValue(groupId, out var groupQueue))
                {
                    continue;
                }

                while (groupQueue.TryDequeue(out var message))
                {
                    ReceiveMessageImpl(message, ref messages, queue, visibilityTimeout, requestedSystemAttributes, requestedMessageAttributes);

                    if (messages is not null && messages.Count >= maxMessages)
                    {
                        break;
                    }

                    // A zero visibility timeout can already have put the message back, in a rebuilt
                    // group queue: stop here, so nothing behind it in the group overtakes it.
                    if (!queue.MessageGroups.TryGetValue(groupId, out var currentQueue) || !ReferenceEquals(currentQueue, groupQueue))
                    {
                        break;
                    }
                }
            }
        }

        return messages;
    }

    private static bool IsAtMaxReceiveCount(Message message, SqsQueueResource queue)
    {
        var receiveCount = message.Attributes?.GetValueOrDefault(InternalMessageSystemAttributeName.ApproximateReceiveCount) ?? "0";
        return queue.MaxReceiveCount is not null && int.Parse(receiveCount, NumberFormatInfo.InvariantInfo) >= queue.MaxReceiveCount;
    }

    private static Message CloneMessage(Message source)
    {
        return new Message
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

    private static void IncrementReceiveCount(Message message)
    {
        var receiveCount = message.Attributes?.GetValueOrDefault(InternalMessageSystemAttributeName.ApproximateReceiveCount) ?? "0";

        var newCount = (int.Parse(receiveCount, NumberFormatInfo.InvariantInfo) + 1).ToString(NumberFormatInfo.InvariantInfo);
        message.Attributes ??= [];
        message.Attributes[InternalMessageSystemAttributeName.ApproximateReceiveCount] = newCount;
    }

    /// <summary>
    /// Keeps only the message attributes the receive asked for, as SQS does: none unless named,
    /// all for <c>All</c> or <c>.*</c>, names starting with the prefix for <c>prefix.*</c>, and
    /// otherwise exact (case-sensitive) names. The digest covers just the attributes returned.
    /// </summary>
    private static void FilterMessageAttributes(Message message, List<string>? requestedMessageAttributes)
    {
        if (message.MessageAttributes is { Count: > 0 } attributes && requestedMessageAttributes is { Count: > 0 })
        {
            var returnAll = requestedMessageAttributes.Exists(name => name is "All" or ".*");
            message.MessageAttributes = returnAll
                ? attributes
                : attributes
                    .Where(attribute => requestedMessageAttributes.Exists(name => MatchesAttributeName(name, attribute.Key)))
                    .ToDictionary();
        }
        else
        {
            message.MessageAttributes = null;
        }

        if (message.MessageAttributes is { Count: 0 })
        {
            message.MessageAttributes = null;
        }

        message.MD5OfMessageAttributes = SqsMessageAttributeDigest.Compute(message.MessageAttributes);

        static bool MatchesAttributeName(string requested, string attributeName) =>
            requested.EndsWith(".*", StringComparison.Ordinal)
                ? attributeName.StartsWith(requested[..^2], StringComparison.Ordinal)
                : string.Equals(requested, attributeName, StringComparison.Ordinal);
    }

    private static void FilterSystemAttributes(Message message, List<string>? requestedSystemAttributes)
    {
        if (requestedSystemAttributes is null || requestedSystemAttributes.Count == 0)
        {
            message.Attributes = ((Dictionary<string, string>?)null).ToInitializedDictionary();
            return;
        }

        if (requestedSystemAttributes.Contains("All"))
        {
            return; // Keep all attributes
        }

        if (message.Attributes is not null)
        {
            var attributesToRemove = message.Attributes.Keys
                .Where(key => !requestedSystemAttributes.Contains(key))
                .ToList();

            foreach (var key in attributesToRemove)
            {
                message.Attributes.Remove(key);
            }
        }
    }
}
