#pragma warning disable CS8600, CS8601, CS8602, CS8604, CS8619 // Nullable reference warnings - internal POCOs use nullable properties but values are set at runtime

using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using LocalSqsSnsMessaging.Sqs.Model;

namespace LocalSqsSnsMessaging;

internal sealed partial class InternalSqsClient
{
    private readonly InMemoryAwsBus _bus;

    /// <summary>The region this client serves: it sees, creates and wires only this region's queues.</summary>
    private readonly string _region;

    private const int MaxMessageSize = 1_048_576; // 1MB

    /// <param name="bus">The bus holding the queues.</param>
    /// <param name="region">
    /// The region the request was made in; <see langword="null"/> for the bus's
    /// <see cref="InMemoryAwsBus.CurrentRegion"/>.
    /// </param>
    internal InternalSqsClient(InMemoryAwsBus bus, string? region = null)
    {
        _bus = bus;
        _region = bus.IsolateRegions && !string.IsNullOrEmpty(region) ? region! : bus.CurrentRegion;
    }

    /// <summary>The queues in this client's region, keyed by name (empty when none was ever created there).</summary>
    private ConcurrentDictionary<string, SqsQueueResource> Queues => _bus.FindQueuesInRegion(_region) ?? [];

    public Task<ChangeMessageVisibilityResponse> ChangeMessageVisibilityAsync(ChangeMessageVisibilityRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var queueName = GetQueueNameFromUrl(request.QueueUrl!);
        if (!Queues.TryGetValue(queueName, out var queue))
        {
            throw new InternalQueueDoesNotExistException();
        }
        Authorize(queue, SqsActionName.ChangeMessageVisibility);

        var error = ChangeVisibilityByReceiptHandle(queue, request.ReceiptHandle, request.VisibilityTimeout.GetValueOrDefault());
        if (error is not null)
        {
            throw error;
        }

        _bus.RecordOperation(AwsServiceName.Sqs, SqsActionName.ChangeMessageVisibility, queue.Arn);
        return Task.FromResult(new ChangeMessageVisibilityResponse().SetCommonProperties());
    }

    /// <summary>What a receipt handle refers to on a queue, as SQS distinguishes it.</summary>
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

    private static ReceiptHandleState ResolveReceiptHandle(SqsQueueResource queue, string? receiptHandle, out string? messageId)
    {
        messageId = null;
        if (receiptHandle is null)
        {
            return ReceiptHandleState.Invalid;
        }

        if (queue.InFlightMessages.ContainsKey(receiptHandle))
        {
            return ReceiptHandleState.Current;
        }

        if (!TryParseReceiptHandle(receiptHandle, out var queueArn, out messageId, out _, out var incarnation))
        {
            return ReceiptHandleState.Invalid;
        }

        if (!queueArn.Equals(queue.Arn, StringComparison.OrdinalIgnoreCase))
        {
            return ReceiptHandleState.Foreign;
        }

        if (!string.Equals(incarnation, queue.Incarnation, StringComparison.Ordinal))
        {
            return ReceiptHandleState.DeletedQueue;
        }

        if (!queue.LatestReceiptHandles.TryGetValue(messageId, out var latestReceiptHandle))
        {
            return ReceiptHandleState.Gone;
        }

        return string.Equals(latestReceiptHandle, receiptHandle, StringComparison.Ordinal)
            ? ReceiptHandleState.Lapsed
            : ReceiptHandleState.Stale;
    }

    /// <summary>
    /// Reads the queue ARN, MessageId, receive time and queue incarnation out of a receipt handle
    /// minted by <see cref="CreateReceiptHandle"/>.
    /// </summary>
    private static bool TryParseReceiptHandle(string receiptHandle, out string queueArn, out string messageId, out DateTimeOffset receivedAt, out string incarnation)
    {
        queueArn = string.Empty;
        messageId = string.Empty;
        receivedAt = default;
        incarnation = string.Empty;
#if NETSTANDARD2_0
        byte[] buffer;
        try
        {
            buffer = Convert.FromBase64String(receiptHandle);
        }
        catch (FormatException)
        {
            return false;
        }
        var decoded = Encoding.UTF8.GetString(buffer);
#else
        var bufferLength = receiptHandle.Length * 3 / 4;
        var buffer = bufferLength <= 1024 ? stackalloc byte[bufferLength] : new byte[bufferLength];
        if (!Convert.TryFromBase64String(receiptHandle, buffer, out var written))
        {
            return false;
        }
        var decoded = Encoding.UTF8.GetString(buffer[..written]);
#endif
        if (decoded.Split(' ') is not [_, var arn, var id, var receivedSeconds, var queueIncarnation]
            || !double.TryParse(receivedSeconds, NumberStyles.Float, NumberFormatInfo.InvariantInfo, out var seconds))
        {
            return false;
        }

        queueArn = arn;
        messageId = id;
        incarnation = queueIncarnation;
        receivedAt = DateTimeOffset.FromUnixTimeMilliseconds((long)Math.Round(seconds * 1000));
        return true;
    }

    private static AwsServiceException InvalidReceiptHandle(ReceiptHandleState state, string? receiptHandle) => state switch
    {
        // The queue the handle was issued by no longer exists, whatever now has its name.
        ReceiptHandleState.DeletedQueue => new InternalQueueDoesNotExistException(),
        ReceiptHandleState.Foreign => new InternalReceiptHandleIsInvalidException($"The receipt handle \"{receiptHandle}\" is not valid for this queue."),
        _ => new InternalReceiptHandleIsInvalidException("The input receipt handle is invalid."),
    };

    private static InternalInvalidParameterValueException ExpiredReceiptHandle(string receiptHandle) =>
        new($"Value {receiptHandle} for parameter ReceiptHandle is invalid. Reason: The receipt handle has expired.");

    /// <summary>
    /// Deletes the message a receipt handle refers to, following SQS: the current or lapsed handle
    /// deletes it; a handle whose message is already gone succeeds; a stale handle succeeds without
    /// deleting on a standard queue and is refused on a FIFO queue.
    /// </summary>
    /// <returns>The error to report, or <see langword="null"/> on success.</returns>
    private static AwsServiceException? DeleteByReceiptHandle(SqsQueueResource queue, string? receiptHandle)
    {
        var state = ResolveReceiptHandle(queue, receiptHandle, out var messageId);
        if (state is ReceiptHandleState.Current or ReceiptHandleState.Lapsed)
        {
            queue.MarkReceiveAttemptModified(receiptHandle);
        }

        switch (state)
        {
            case ReceiptHandleState.Current:
                if (queue.InFlightMessages.TryRemove(receiptHandle!, out var inFlightInfo))
                {
                    var (message, expirationHandler) = inFlightInfo;
                    queue.LatestReceiptHandles.TryRemove(message.MessageId!, out _);
                    FinalizeDeletedMessage(queue, message, expirationHandler);
                }

                return null;
            case ReceiptHandleState.Lapsed:
                if (queue.TryTakeVisibleMessage(messageId!, out var visibleMessage))
                {
                    queue.LatestReceiptHandles.TryRemove(messageId!, out _);
                }

                return null;
            case ReceiptHandleState.Stale:
                return queue.IsFifo ? ExpiredReceiptHandle(receiptHandle!) : null;
            case ReceiptHandleState.Gone:
                return null;
            default:
                return InvalidReceiptHandle(state, receiptHandle);
        }
    }

    /// <summary>
    /// Changes the visibility of the message a receipt handle refers to, following SQS: the current
    /// handle changes it; a lapsed handle re-hides the visible message; a stale handle, or one whose
    /// message is gone, is refused.
    /// </summary>
    /// <returns>The error to report, or <see langword="null"/> on success.</returns>
    private AwsServiceException? ChangeVisibilityByReceiptHandle(SqsQueueResource queue, string? receiptHandle, int visibilityTimeoutSeconds)
    {
        try
        {
            SqsRequestValidation.ValidateVisibilityTimeout(visibilityTimeoutSeconds);
        }
        catch (AwsServiceException error)
        {
            return error;
        }

        var visibilityTimeout = TimeSpan.FromSeconds(visibilityTimeoutSeconds);
        var state = ResolveReceiptHandle(queue, receiptHandle, out var messageId);
        if (state is ReceiptHandleState.Current or ReceiptHandleState.Lapsed
            && TryParseReceiptHandle(receiptHandle!, out _, out _, out var receivedAt, out _)
            && SqsRequestValidation.CheckVisibilityCap(visibilityTimeoutSeconds, _bus.TimeProvider.GetUtcNow() - receivedAt) is { } capError)
        {
            return capError;
        }

        if (state is ReceiptHandleState.Current or ReceiptHandleState.Lapsed)
        {
            queue.MarkReceiveAttemptModified(receiptHandle);
        }

        switch (state)
        {
            case ReceiptHandleState.Current:
                if (queue.InFlightMessages.TryGetValue(receiptHandle!, out var inFlightInfo))
                {
                    var (message, expirationHandler) = inFlightInfo;
                    if (visibilityTimeout == TimeSpan.Zero)
                    {
                        if (queue.InFlightMessages.TryRemove(receiptHandle!, out var removedInfo))
                        {
                            removedInfo.Item2.Dispose();
                            // Same path as visibility-timeout expiry: for FIFO queues the message is
                            // re-inserted at its original position in the group (not appended) and
                            // the group's in-flight slot is released.
                            queue.ReturnInFlightMessage(message);
                        }
                    }
                    else
                    {
                        expirationHandler.UpdateTimeout(visibilityTimeout);
                    }
                }

                return null;
            case ReceiptHandleState.Lapsed:
                if (visibilityTimeout > TimeSpan.Zero && queue.TryTakeVisibleMessage(messageId!, out var visibleMessage))
                {
                    // The handle is still the message's latest, so it hides the message again.
                    var expirationJob = new SqsInflightMessageExpirationJob(receiptHandle!, queue, Timeout.InfiniteTimeSpan, _bus.TimeProvider);
                    if (queue.IsFifo && visibleMessage!.Attributes?.TryGetValue("MessageGroupId", out var groupId) == true)
                    {
                        lock (queue.MessageGroupLocks.GetOrAdd(groupId, _ => new object()))
                        {
                            queue.InFlightGroupCounts.AddOrUpdate(groupId, 1, (_, count) => count + 1);
                        }
                    }

                    queue.InFlightMessages[receiptHandle!] = (visibleMessage!, expirationJob);
                    expirationJob.UpdateTimeout(visibilityTimeout);
                }

                return null;
            case ReceiptHandleState.Stale:
                return ExpiredReceiptHandle(receiptHandle!);
            case ReceiptHandleState.Gone:
                return new InternalInvalidParameterValueException(
                    $"Value {receiptHandle} for parameter ReceiptHandle is invalid. Reason: Message does not exist or is not available for visibility timeout change.");
            default:
                return InvalidReceiptHandle(state, receiptHandle);
        }
    }

    public Task<CreateQueueResponse> CreateQueueAsync(CreateQueueRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var fifo = SqsQueueValidation.RequestsFifo(request.Attributes);
        SqsQueueValidation.ValidateQueueName(request.QueueName, fifo);
        var requestedAttributes = request.Attributes ?? [];
        SqsQueueValidation.ValidateAttributes(requestedAttributes, fifo, creating: true, current: new Dictionary<string, string>());
        SqsQueueValidation.ValidateRedriveAllowPolicyRegion(requestedAttributes, _region);
        if (request.Tags is { Count: > 0 })
        {
            SqsQueueValidation.ValidateTags(request.Tags);
            if (request.Tags.Count > SqsQueueValidation.MaxTagsPerQueue)
            {
                throw SqsQueueValidation.TooManyTags(request.QueueName!);
            }
        }

        if (Queues.TryGetValue(request.QueueName!, out var existingQueue))
        {
            // An existing name is only reused when everything asked for matches the queue as it is now.
            EnsureMatchesExistingQueue(existingQueue, requestedAttributes, request.Tags);
            _bus.RecordOperation(AwsServiceName.Sqs, SqsActionName.CreateQueue, existingQueue.Arn);
            return Task.FromResult(new CreateQueueResponse { QueueUrl = existingQueue.Url }.SetCommonProperties());
        }

        var queueUrl = _bus.ServiceUrl is not null
            ? $"{_bus.ServiceUrl.ToString().TrimEnd('/')}/{_bus.CurrentAccountId}/{request.QueueName}"
            : $"https://sqs.{_region}.amazonaws.com/{_bus.CurrentAccountId}/{request.QueueName}";
        var visibilityTimeoutParsed = request.Attributes?.TryGetValue(InternalQueueAttributeName.VisibilityTimeout, out var visibilityTimeout) == true
            ? TimeSpan.FromSeconds(int.Parse(visibilityTimeout, NumberFormatInfo.InvariantInfo))
            : TimeSpan.FromSeconds(30);

        var now = _bus.TimeProvider.GetUtcNow();
        var queue = new SqsQueueResource
        {
            Name = request.QueueName!,
            Region = _region,
            AccountId = _bus.CurrentAccountId,
            Url = queueUrl,
            VisibilityTimeout = visibilityTimeoutParsed,
            CreatedTimestamp = now,
            LastModifiedTimestamp = now,
            Attributes = [],
            Tags = request.Tags is { Count: > 0 } tags ? new Dictionary<string, string>(tags) : null
        };

        if (requestedAttributes.TryGetValue(InternalQueueAttributeName.RedrivePolicy, out var redrivePolicy))
        {
            ValidateRedrivePolicy(queue, redrivePolicy);
        }

        foreach (var requestAttribute in requestedAttributes)
        {
            queue.Attributes[requestAttribute.Key] = requestAttribute.Value;
        }

        StorePolicy(queue.Attributes);
        AddDefaultAttributes(queue);
        queue.Attributes.Add("QueueArn", queue.Arn);
        UpdateQueueProperties(queue);
        var regionQueues = _bus.GetOrAddQueuesInRegion(_region);
        if (!regionQueues.TryAdd(request.QueueName!, queue))
        {
            // Created concurrently under the same name: answer as for an existing queue.
            var winner = regionQueues[request.QueueName!];
            EnsureMatchesExistingQueue(winner, requestedAttributes, request.Tags);
            return Task.FromResult(new CreateQueueResponse { QueueUrl = winner.Url }.SetCommonProperties());
        }

        _bus.RecordOperation(AwsServiceName.Sqs, SqsActionName.CreateQueue, queue.Arn);

        var response = new CreateQueueResponse
        {
            QueueUrl = queueUrl
        };

        return Task.FromResult(response.SetCommonProperties());
    }

    /// <summary>
    /// <c>CreateQueue</c> on an existing name returns that queue only when every requested attribute
    /// and tag matches its current value; otherwise SQS refuses it with <c>QueueAlreadyExists</c>.
    /// </summary>
    private static void EnsureMatchesExistingQueue(SqsQueueResource queue, Dictionary<string, string> requestedAttributes, Dictionary<string, string>? requestedTags)
    {
        foreach (var (name, value) in requestedAttributes)
        {
            var requested = name == InternalQueueAttributeName.Policy && !string.IsNullOrEmpty(value) ? SqsQueuePolicy.Normalize(value) : value;
            if (!SqsQueueValidation.SameAttributeValue(name, requested, queue.Attributes?.GetValueOrDefault(name)))
            {
                throw SqsRequestValidation.Error("QueueNameExists",
                    $"A queue already exists with the same name and a different value for attribute {name}");
            }
        }

        if (requestedTags is { Count: > 0 })
        {
            bool sameTags;
            lock (queue.TagsLock)
            {
                sameTags = queue.Tags is { } tags && tags.Count == requestedTags.Count
                           && requestedTags.All(tag => tags.TryGetValue(tag.Key, out var value) && value == tag.Value);
            }

            if (!sameTags)
            {
                throw SqsRequestValidation.Error("QueueNameExists",
                    "A queue already exists with the same name and a different value for tags ");
            }
        }
    }

    /// <summary>The queue an ARN names, if it is one of this bus's queues.</summary>
    private SqsQueueResource? FindQueueByArn(string? arn)
    {
        if (arn is null)
        {
            return null;
        }

        var name = arn[(arn.LastIndexOf(':') + 1)..];
        return Queues.TryGetValue(name, out var queue) && string.Equals(queue.Arn, arn, StringComparison.Ordinal)
            ? queue
            : null;
    }

    /// <summary>
    /// Checks a <c>RedrivePolicy</c> being set on <paramref name="source"/>: well formed, naming an
    /// existing queue of the same type whose <c>RedriveAllowPolicy</c> admits the source.
    /// </summary>
    private void ValidateRedrivePolicy(SqsQueueResource source, string value)
    {
        if (value.Length == 0)
        {
            return;
        }

        var policy = SqsQueueValidation.ParseRedrivePolicy(value);
        if (SqsQueueValidation.ArnRegion(policy.DeadLetterTargetArn) is { } targetRegion
            && !string.Equals(targetRegion, source.Region, StringComparison.Ordinal))
        {
            throw SqsRequestValidation.InvalidParameterValue("RedrivePolicy", value,
                "Dead-letter target must be in same region as the source.");
        }

        var deadLetterQueue = FindQueueByArn(policy.DeadLetterTargetArn)
            ?? throw SqsRequestValidation.InvalidParameterValue("RedrivePolicy", value, "Dead letter target does not exist.");
        if (deadLetterQueue.IsFifo != source.IsFifo)
        {
            throw SqsRequestValidation.InvalidParameterValue("RedrivePolicy", value,
                "Dead-letter queue must be same type of queue as the source..");
        }

        if (!SqsQueueValidation.RedriveAllowed(deadLetterQueue, source.Arn))
        {
            throw SqsQueueValidation.RedriveNotAllowed(policy);
        }
    }

    /// <summary>
    /// Fills in the attributes real SQS reports for a queue that was created without them, so
    /// <c>GetQueueAttributes</c> returns its defaults rather than omitting them.
    /// </summary>
    private static void AddDefaultAttributes(SqsQueueResource queue)
    {
        var attributes = queue.Attributes!;
        attributes.TryAdd(InternalQueueAttributeName.DelaySeconds, "0");
        attributes.TryAdd(InternalQueueAttributeName.MaximumMessageSize, "1048576");
        attributes.TryAdd(InternalQueueAttributeName.MessageRetentionPeriod, "345600");
        attributes.TryAdd(InternalQueueAttributeName.ReceiveMessageWaitTimeSeconds, "0");
        attributes.TryAdd(InternalQueueAttributeName.VisibilityTimeout, "30");

        if (attributes.ContainsKey(InternalQueueAttributeName.KmsMasterKeyId))
        {
            attributes.TryAdd(InternalQueueAttributeName.KmsDataKeyReusePeriodSeconds, "300");
            attributes.TryAdd(InternalQueueAttributeName.SqsManagedSseEnabled, "false");
        }
        else
        {
            attributes.TryAdd(InternalQueueAttributeName.SqsManagedSseEnabled, "true");
        }

        if (queue.IsFifo)
        {
            attributes.TryAdd(InternalQueueAttributeName.FifoQueue, "true");
            attributes.TryAdd(InternalQueueAttributeName.ContentBasedDeduplication, "false");
            attributes.TryAdd(InternalQueueAttributeName.DeduplicationScope, "queue");
            attributes.TryAdd(InternalQueueAttributeName.FifoThroughputLimit, "perQueue");
        }
    }

    public Task<DeleteMessageResponse> DeleteMessageAsync(DeleteMessageRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var queueName = GetQueueNameFromUrl(request.QueueUrl!);
        if (!Queues.TryGetValue(queueName, out var queue))
        {
            throw new InternalQueueDoesNotExistException();
        }
        Authorize(queue, SqsActionName.DeleteMessage);

        var error = DeleteByReceiptHandle(queue, request.ReceiptHandle);
        if (error is not null)
        {
            throw error;
        }

        _bus.RecordOperation(AwsServiceName.Sqs, SqsActionName.DeleteMessage, queue.Arn);
        return Task.FromResult(new DeleteMessageResponse().SetCommonProperties());
    }

    /// <summary>
    /// Cleanup shared by the single and batch delete paths once a message has been removed
    /// from the in-flight set: stops the visibility-timeout timer and, for FIFO queues,
    /// releases the message's deduplication id and its message group's in-flight slot so
    /// the group unlocks for subsequent receives.
    /// </summary>
    private static void FinalizeDeletedMessage(SqsQueueResource queue, Message message, SqsInflightMessageExpirationJob expirationHandler)
    {
        expirationHandler.Dispose();

        if (!queue.IsFifo)
        {
            return;
        }

        // The deduplication id stays claimed for the rest of its interval.
        queue.ReleaseDeletedFifoMessage(message);
    }

    public Task<DeleteQueueResponse> DeleteQueueAsync(DeleteQueueRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var queueName = GetQueueNameFromUrl(request.QueueUrl!);
        if (Queues.TryGetValue(queueName, out var queueToDelete))
        {
            Authorize(queueToDelete, SqsActionName.DeleteQueue);
        }

        if (!Queues.TryRemove(queueName, out var removedQueue))
        {
            throw new InternalQueueDoesNotExistException();
        }

        _bus.RecordOperation(AwsServiceName.Sqs, SqsActionName.DeleteQueue, removedQueue.Arn);
        return Task.FromResult(new DeleteQueueResponse().SetCommonProperties());
    }

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

        if (IsAtMaxReceiveCount(message, queue) && queue.ErrorQueue is not null
            && SqsQueueValidation.RedriveAllowed(queue.ErrorQueue, queue.Arn))
        {
            message.Attributes ??= [];
            message.Attributes[InternalMessageSystemAttributeName.DeadLetterQueueSourceArn] = queue.Arn;
            if (queue.ErrorQueue.IsFifo)
            {
                // In a FIFO dead-letter queue the message is deduplicated by its original id, and
                // its retention period starts over.
                message.Attributes[InternalMessageSystemAttributeName.MessageDeduplicationId] = message.MessageId!;
                queue.ErrorQueue.RestartRetention(message, _bus.TimeProvider.GetUtcNow());
            }

            queue.LatestReceiptHandles.TryRemove(message.MessageId!, out _);
            EnqueueMessage(queue.ErrorQueue, message);
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

    private static bool IsFairQueue(SqsQueueResource queue)
    {
        return queue.Attributes != null &&
               queue.Attributes.TryGetValue(InternalQueueAttributeName.DeduplicationScope, out var dedupScope) &&
               dedupScope == "messageGroup" &&
               queue.Attributes.TryGetValue(InternalQueueAttributeName.FifoThroughputLimit, out var throughputLimit) &&
               throughputLimit == "perMessageGroupId";
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

    private string CreateReceiptHandle(Message message, SqsQueueResource queue)
    {
#pragma warning disable CA1308
        var guid = Guid.NewGuid().ToString().ToLowerInvariant();
#pragma warning restore CA1308
        // Invariant: TryParseReceiptHandle reads the timestamp back with the invariant culture.
        var decodedReceiptHandle = FormattableString.Invariant(
            $"{guid} {queue.Arn} {message.MessageId} {_bus.TimeProvider.GetUtcNow().ToUnixTimeMilliseconds() / 1000.0} {queue.Incarnation}");
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(decodedReceiptHandle));
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

    public Task<SendMessageResponse> SendMessageAsync(SendMessageRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var queueName = GetQueueNameFromUrl(request.QueueUrl!);
        if (!Queues.TryGetValue(queueName, out var queue))
        {
            throw new InternalQueueDoesNotExistException();
        }
        Authorize(queue, SqsActionName.SendMessage);

        if (SqsRequestValidation.ValidateSend(queue, new SqsRequestValidation.SendParameters(
                request.MessageBody, request.DelaySeconds, request.MessageGroupId, request.MessageDeduplicationId,
                request.MessageAttributes, request.MessageSystemAttributes)) is { } error)
        {
            throw error;
        }

        EnsureKmsKeyExists(queue);

        var message = CreateMessage(request.MessageBody!, request.MessageAttributes, request.MessageSystemAttributes);

        if (queue.IsFifo)
        {
            if (!TryEnqueueFifoMessage(queue, message, request.MessageGroupId!,
                    request.MessageDeduplicationId, request.MessageBody!, _bus.TimeProvider, out var original))
            {
                return Task.FromResult(new SendMessageResponse
                {
                    MessageId = original.MessageId,
                    SequenceNumber = original.SequenceNumber,
                    MD5OfMessageBody = message.MD5OfBody,
                    MD5OfMessageAttributes = SqsMessageAttributeDigest.Compute(request.MessageAttributes),
                    MD5OfMessageSystemAttributes = SqsMessageAttributeDigest.Compute(request.MessageSystemAttributes)
                }.SetCommonProperties());
            }
        }
        else
        {
            if (request.MessageGroupId is not null)
            {
                // A standard queue keeps a MessageGroupId (for fair queueing) without ordering by it.
                message.Attributes![InternalMessageSystemAttributeName.MessageGroupId] = request.MessageGroupId;
            }

            // A message's own DelaySeconds overrides the queue's, in either direction.
            var delaySeconds = request.DelaySeconds ?? queue.DelaySeconds;
            if (delaySeconds > 0)
            {
                if (request.DelaySeconds > 0)
                {
                    message.Attributes ??= [];
                    message.Attributes["DelaySeconds"] = delaySeconds.ToString(NumberFormatInfo.InvariantInfo);
                }

                queue.EnqueueAfterDelay(message, TimeSpan.FromSeconds(delaySeconds), _bus.TimeProvider);
            }
            else
            {
                queue.Messages.Enqueue(message);
            }
        }

        _bus.RecordOperation(AwsServiceName.Sqs, SqsActionName.SendMessage, queue.Arn);
        return Task.FromResult(new SendMessageResponse
        {
            MessageId = message.MessageId,
            MD5OfMessageBody = message.MD5OfBody,
            MD5OfMessageAttributes = SqsMessageAttributeDigest.Compute(request.MessageAttributes),
            MD5OfMessageSystemAttributes = SqsMessageAttributeDigest.Compute(request.MessageSystemAttributes),
            SequenceNumber = message.Attributes?.GetValueOrDefault("SequenceNumber")
        }.SetCommonProperties());
    }

    private static void EnqueueMessage(SqsQueueResource targetQueue, Message message)
    {
        if (targetQueue.IsFifo)
        {
            if (!message.Attributes!.TryGetValue("MessageGroupId", out var groupId) || string.IsNullOrEmpty(groupId))
            {
                throw new InvalidOperationException("Message destined for a FIFO queue must have a MessageGroupId.");
            }
            targetQueue.EnqueueFifoMessage(groupId, message);
        }
        else
        {
            targetQueue.Messages.Enqueue(message);
        }
    }

    /// <summary>
    /// Applies FIFO group assignment and (content-based or explicit) deduplication, enqueuing the
    /// message via <see cref="SqsQueueResource.EnqueueFifoMessage"/> when it is not a duplicate. Shared by the single
    /// and batch send paths so both behave identically for FIFO queues.
    /// </summary>
    /// <returns>
    /// <see langword="true"/> when the message was enqueued; <see langword="false"/> when it was a
    /// duplicate, in which case <paramref name="original"/> is the message that claimed the
    /// deduplication id.
    /// </returns>
    private static bool TryEnqueueFifoMessage(SqsQueueResource queue, Message message, string messageGroupId,
        string? messageDeduplicationId, string messageBody, TimeProvider timeProvider, out SqsDeduplicationCache.Entry original)
    {
        message.Attributes ??= [];
        message.Attributes["MessageGroupId"] = messageGroupId;

        var deduplicationId = messageDeduplicationId;
        if (string.IsNullOrEmpty(deduplicationId))
        {
            deduplicationId = SqsDeduplicationCache.ContentDeduplicationId(messageBody);
        }

        message.Attributes[InternalMessageSystemAttributeName.MessageDeduplicationId] = deduplicationId;

        return queue.TryEnqueueFifoSend(message, messageGroupId, deduplicationId, IsFairQueue(queue), timeProvider, out original);
    }

    private Message CreateMessage(string messageBody, Dictionary<string, MessageAttributeValue>? messageAttributes, Dictionary<string, MessageSystemAttributeValue>? messageSystemAttributes)
    {
        // Validated by SqsRequestValidation.ValidateSend. SQS keeps a well-formed AWSTraceHeader
        // and silently drops a malformed one; it stores Number attributes in plain notation.
        Dictionary<string, string>? attributes = null;
        if (messageSystemAttributes?.TryGetValue(InternalMessageSystemAttributeName.AWSTraceHeader, out var traceHeader) == true
            && SqsRequestValidation.IsWellFormedTraceHeader(traceHeader))
        {
            attributes = new() { [InternalMessageSystemAttributeName.AWSTraceHeader] = traceHeader.StringValue };
        }

        messageAttributes = NormalizeNumberAttributes(messageAttributes);

        // Populate server-side system attributes that real AWS sets at SendMessage time:
        //   - SentTimestamp: epoch milliseconds the request was accepted
        //   - SenderId: the caller's IAM user id (see SqsCaller.SenderId); the raw client and
        //     unsigned requests are attributed to the bus's CurrentAccountId
        attributes ??= [];
        attributes[InternalMessageSystemAttributeName.SentTimestamp] =
            _bus.TimeProvider.GetUtcNow().ToUnixTimeMilliseconds().ToString(NumberFormatInfo.InvariantInfo);
        attributes[InternalMessageSystemAttributeName.SenderId] = (Caller ?? SqsCaller.Unsigned).SenderId(_bus);

        var message = new Message
        {
            MessageId = Guid.NewGuid().ToString(),
            Body = messageBody,
            MessageAttributes = messageAttributes,
            Attributes = attributes
        };

#pragma warning disable CA5351
        var hash = MD5.HashData(Encoding.UTF8.GetBytes(messageBody));
#pragma warning restore CA5351
#pragma warning disable CA1308
        message.MD5OfBody = Convert.ToHexString(hash).ToLowerInvariant();
#pragma warning restore CA1308

        return message;
    }

    /// <summary>The attributes as stored: a copy with Number values in plain notation, when any needs it.</summary>
    private static Dictionary<string, MessageAttributeValue>? NormalizeNumberAttributes(Dictionary<string, MessageAttributeValue>? messageAttributes)
    {
        if (messageAttributes is null)
        {
            return null;
        }

        Dictionary<string, MessageAttributeValue>? normalized = null;
        foreach (var (name, value) in messageAttributes)
        {
            if (value.DataType?.StartsWith("Number", StringComparison.Ordinal) != true || value.StringValue is null)
            {
                continue;
            }

            var plain = SqsRequestValidation.NormalizeNumber(value.StringValue);
            if (!string.Equals(plain, value.StringValue, StringComparison.Ordinal))
            {
                normalized ??= new Dictionary<string, MessageAttributeValue>(messageAttributes);
                normalized[name] = new MessageAttributeValue { DataType = value.DataType, StringValue = plain };
            }
        }

        return normalized ?? messageAttributes;
    }

    public Task<CancelMessageMoveTaskResponse> CancelMessageMoveTaskAsync(CancelMessageMoveTaskRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!_bus.MoveTasks.TryGetValue(request.TaskHandle!, out var task))
        {
            throw new InternalResourceNotFoundException("Task does not exist.");
        }

        task.MoveTaskJob.Dispose();
        task.Status = MoveTaskStatus.Cancelled;

        _bus.RecordOperation(AwsServiceName.Sqs, SqsActionName.CancelMessageMoveTask, task.SourceQueue.Arn);
        return Task.FromResult(new CancelMessageMoveTaskResponse
        {
            ApproximateNumberOfMessagesMoved = task.ApproximateNumberOfMessagesMoved
        }.SetCommonProperties());
    }

    public Task<ChangeMessageVisibilityBatchResponse> ChangeMessageVisibilityBatchAsync(
        ChangeMessageVisibilityBatchRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var queueName = GetQueueNameFromUrl(request.QueueUrl!);
        if (!Queues.TryGetValue(queueName, out var queue))
        {
            throw new InternalQueueDoesNotExistException();
        }
        Authorize(queue, SqsActionName.ChangeMessageVisibility);

        SqsRequestValidation.ValidateBatchRequest(request.Entries, entry => entry.Id, "ChangeMessageVisibilityBatchRequestEntry");

        var response = new ChangeMessageVisibilityBatchResponse
        {
            Successful = [],
            Failed = []
        };

        foreach (var entry in request.Entries!)
        {
            var error = ChangeVisibilityByReceiptHandle(queue, entry.ReceiptHandle, entry.VisibilityTimeout.GetValueOrDefault());
            if (error is null)
            {
                response.Successful.Add(new ChangeMessageVisibilityBatchResultEntry
                {
                    Id = entry.Id
                });
            }
            else
            {
                response.Failed.Add(new BatchResultErrorEntry
                {
                    Id = entry.Id,
                    Code = error.ErrorCode,
                    Message = error.Message,
                    SenderFault = true
                });
            }
        }

        _bus.RecordOperation(AwsServiceName.Sqs, SqsActionName.ChangeMessageVisibilityBatch, queue.Arn);
        return Task.FromResult(response.SetCommonProperties());
    }

    public Task<DeleteMessageBatchResponse> DeleteMessageBatchAsync(DeleteMessageBatchRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var queueName = GetQueueNameFromUrl(request.QueueUrl!);
        if (!Queues.TryGetValue(queueName, out var queue))
        {
            throw new InternalQueueDoesNotExistException();
        }
        Authorize(queue, SqsActionName.DeleteMessage);

        SqsRequestValidation.ValidateBatchRequest(request.Entries, entry => entry.Id, "DeleteMessageBatchRequestEntry");

        var response = new DeleteMessageBatchResponse
        {
            Successful = [],
            Failed = []
        };

        foreach (var entry in request.Entries!)
        {
            var error = DeleteByReceiptHandle(queue, entry.ReceiptHandle);
            if (error is null)
            {
                response.Successful.Add(new DeleteMessageBatchResultEntry
                {
                    Id = entry.Id
                });
            }
            else
            {
                response.Failed.Add(new BatchResultErrorEntry
                {
                    Id = entry.Id,
                    Code = error.ErrorCode,
                    Message = error.Message,
                    SenderFault = true
                });
            }
        }

        _bus.RecordOperation(AwsServiceName.Sqs, SqsActionName.DeleteMessageBatch, queue.Arn);
        return Task.FromResult(response.SetCommonProperties());
    }

    public Task<GetQueueAttributesResponse> GetQueueAttributesAsync(GetQueueAttributesRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var queueName = GetQueueNameFromUrl(request.QueueUrl!);
        if (!Queues.TryGetValue(queueName, out var queue))
        {
            throw new InternalQueueDoesNotExistException();
        }
        Authorize(queue, SqsActionName.GetQueueAttributes);

        // Expired messages are otherwise only dropped when a receive reaches them; sweep them
        // now so the approximate counters don't keep reporting them.
        queue.RemoveExpiredMessages(_bus.TimeProvider.GetUtcNow());

        var attributes = new Dictionary<string, string>();
        var attributeNames = request.AttributeNames ?? [];
        SqsQueueValidation.ValidateAttributeNamesToGet(attributeNames);

        if (attributeNames.Count == 0 || attributeNames.Contains("All"))
        {
            attributes = new Dictionary<string, string>(queue.Attributes ?? []);
            AddComputedAttributes(queue, attributes);
        }
        else
        {
            foreach (var attributeName in attributeNames)
            {
                if (queue.Attributes?.TryGetValue(attributeName, out var value) == true)
                {
                    attributes[attributeName] = value;
                }
                else if (IsComputedAttribute(attributeName))
                {
                    AddComputedAttribute(queue, attributeName, attributes);
                }
            }
        }

        _bus.RecordOperation(AwsServiceName.Sqs, SqsActionName.GetQueueAttributes, queue.Arn);
        return Task.FromResult(new GetQueueAttributesResponse
        {
            Attributes = attributes
        }.SetCommonProperties());
    }

    private void UpdateQueueProperties(SqsQueueResource queue)
    {
        if (queue.Attributes?.TryGetValue(InternalQueueAttributeName.VisibilityTimeout, out var visibilityTimeout) == true)
        {
            queue.VisibilityTimeout = TimeSpan.FromSeconds(int.Parse(visibilityTimeout, NumberFormatInfo.InvariantInfo));
        }

        if (queue.Attributes?.TryGetValue(InternalQueueAttributeName.MessageRetentionPeriod, out var retentionSeconds) == true)
        {
            queue.MessageRetentionPeriod = TimeSpan.FromSeconds(int.Parse(retentionSeconds, NumberFormatInfo.InvariantInfo));
        }

        if (queue.Attributes?.TryGetValue(InternalQueueAttributeName.DelaySeconds, out var delaySeconds) == true)
        {
            queue.DelaySeconds = int.Parse(delaySeconds, NumberFormatInfo.InvariantInfo);
        }

        if (queue.Attributes?.TryGetValue(InternalQueueAttributeName.ReceiveMessageWaitTimeSeconds, out var waitTimeSeconds) == true)
        {
            queue.ReceiveMessageWaitTime = TimeSpan.FromSeconds(int.Parse(waitTimeSeconds, NumberFormatInfo.InvariantInfo));
        }

        if (queue.Attributes?.TryGetValue(InternalQueueAttributeName.MaximumMessageSize, out var maximumMessageSize) == true)
        {
            queue.MaximumMessageSize = int.Parse(maximumMessageSize, NumberFormatInfo.InvariantInfo);
        }

        queue.ContentBasedDeduplication = queue.Attributes?.TryGetValue(InternalQueueAttributeName.ContentBasedDeduplication, out var contentBasedDeduplication) == true
            && bool.TryParse(contentBasedDeduplication, out var enabled) && enabled;

        ExtractRedrivePolicy(queue);
    }

    private static bool IsComputedAttribute(string attributeName)
    {
        return attributeName == InternalQueueAttributeName.ApproximateNumberOfMessages
               || attributeName == InternalQueueAttributeName.ApproximateNumberOfMessagesNotVisible
               || attributeName == InternalQueueAttributeName.ApproximateNumberOfMessagesDelayed
               || attributeName == InternalQueueAttributeName.CreatedTimestamp
               || attributeName == InternalQueueAttributeName.LastModifiedTimestamp;
    }

    private static void AddComputedAttributes(SqsQueueResource queue, Dictionary<string, string> attributes)
    {
        AddComputedAttribute(queue, InternalQueueAttributeName.ApproximateNumberOfMessages, attributes);
        AddComputedAttribute(queue, InternalQueueAttributeName.ApproximateNumberOfMessagesNotVisible, attributes);
        AddComputedAttribute(queue, InternalQueueAttributeName.ApproximateNumberOfMessagesDelayed, attributes);
        AddComputedAttribute(queue, InternalQueueAttributeName.CreatedTimestamp, attributes);
        AddComputedAttribute(queue, InternalQueueAttributeName.LastModifiedTimestamp, attributes);
    }

    private static void AddComputedAttribute(SqsQueueResource queue, string attributeName, Dictionary<string, string> attributes)
    {
        long? value = attributeName switch
        {
            InternalQueueAttributeName.ApproximateNumberOfMessages => queue.VisibleMessageCount,
            InternalQueueAttributeName.ApproximateNumberOfMessagesNotVisible => queue.InFlightMessages.Count,
            InternalQueueAttributeName.ApproximateNumberOfMessagesDelayed => queue.DelayedMessageCount,
            // Epoch seconds, as SQS reports them.
            InternalQueueAttributeName.CreatedTimestamp => queue.CreatedTimestamp.ToUnixTimeSeconds(),
            InternalQueueAttributeName.LastModifiedTimestamp => queue.LastModifiedTimestamp.ToUnixTimeSeconds(),
            _ => null,
        };

        if (value is not null)
        {
            attributes[attributeName] = value.Value.ToString(NumberFormatInfo.InvariantInfo);
        }
    }

    private static string GetQueueNameFromUrl(string queueUrl)
    {
        return queueUrl.Split('/').Last();
    }

    private static string GetQueueNameFromArn(string queueArn)
    {
        var indexOfLastColon = queueArn.LastIndexOf(':');
        if (indexOfLastColon == -1)
        {
            throw new ArgumentException("ARN malformed", nameof(queueArn));
        }
        return queueArn[(indexOfLastColon+1) ..];
    }

    public Task<GetQueueUrlResponse> GetQueueUrlAsync(GetQueueUrlRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!Queues.TryGetValue(request.QueueName!, out var queue))
        {
            throw new InternalQueueDoesNotExistException();
        }
        Authorize(queue, SqsActionName.GetQueueUrl);
        _bus.RecordOperation(AwsServiceName.Sqs, SqsActionName.GetQueueUrl, queue.Arn);
        return Task.FromResult(new GetQueueUrlResponse { QueueUrl = queue.Url }.SetCommonProperties());
    }

    public Task<ListDeadLetterSourceQueuesResponse> ListDeadLetterSourceQueuesAsync(
        ListDeadLetterSourceQueuesRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var deadLetterQueueName = GetQueueNameFromUrl(request.QueueUrl!);
        if (!Queues.TryGetValue(deadLetterQueueName, out var deadLetterQueue))
        {
            throw new InternalQueueDoesNotExistException();
        }
        Authorize(deadLetterQueue, SqsActionName.ListDeadLetterSourceQueues);

        var deadLetterSourceQueues = Queues.Values
            .Where(q => q.ErrorQueue?.Arn == deadLetterQueue.Arn)
            .Select(q => q.Url)
            .OrderBy(url => url)
            .ToList();

        var pagedQueues = new PaginatedList<string>(deadLetterSourceQueues);

        var (items, nextToken) = pagedQueues.GetPage(
            TokenGenerator,
            request.MaxResults ?? 1000,
            request.NextToken);

        _bus.RecordOperation(AwsServiceName.Sqs, SqsActionName.ListDeadLetterSourceQueues);
        return Task.FromResult(new ListDeadLetterSourceQueuesResponse
        {
            QueueUrls = items,
            NextToken = nextToken
        }.SetCommonProperties());

        static string TokenGenerator(string x)
        {
            return Convert.ToBase64String(Encoding.UTF8.GetBytes(x));
        }
    }

    public Task<ListMessageMoveTasksResponse> ListMessageMoveTasksAsync(ListMessageMoveTasksRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.MaxResults is < 1 or > 10)
        {
            throw SqsRequestValidation.InvalidParameterValue(
                "Value for parameter MaxResults is invalid. Reason: You must enter a number that's between 1 and 10.");
        }

        // Most recent first; SQS returns only the latest task unless MaxResults asks for more.
        var tasks = _bus.MoveTasks.Values
            .Where(t => t.SourceQueue.Arn.Equals(request.SourceArn, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(t => t.Sequence)
            .Take(request.MaxResults ?? 1)
            .Select(t => new ListMessageMoveTasksResultEntry
            {
                TaskHandle = t.TaskHandle,
                SourceArn = t.SourceQueue.Arn,
                DestinationArn = t.DestinationQueue?.Arn,
                MaxNumberOfMessagesPerSecond = t.MaxNumberOfMessagesPerSecond,
                Status = t.Status,
                FailureReason = t.FailureReason,
                StartedTimestamp = t.StartedTimestamp,
                ApproximateNumberOfMessagesMoved = t.ApproximateNumberOfMessagesMoved,
                ApproximateNumberOfMessagesToMove = t.ApproximateNumberOfMessagesToMove
            });

        _bus.RecordOperation(AwsServiceName.Sqs, SqsActionName.ListMessageMoveTasks, request.SourceArn);
        return Task.FromResult(new ListMessageMoveTasksResponse
        {
            Results = tasks.ToList()
        }.SetCommonProperties());
    }

    public Task<ListQueuesResponse> ListQueuesAsync(ListQueuesRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.MaxResults is < 1 or > 1000)
        {
            throw SqsRequestValidation.InvalidParameterValue(
                "Value for parameter MaxResults is invalid. Reason: MaxResults must be an integer between 1 and 1000.");
        }

        if (request.NextToken is { Length: > 0 } requestToken && !IsQueueUrlToken(requestToken))
        {
            throw SqsRequestValidation.InvalidParameterValue("Invalid or expired next token.");
        }

        var allQueues = Queues
            .Values
            .Where(q => string.IsNullOrEmpty(request.QueueNamePrefix) || q.Name.StartsWith(request.QueueNamePrefix, StringComparison.Ordinal))
            .Select(q => q.Url)
            .OrderBy(url => url)
            .ToList();

        var pagedQueues = new PaginatedList<string>(allQueues);

        var (items, nextToken) = pagedQueues.GetPage(
            TokenGenerator,
            request.MaxResults ?? 1000,
            request.NextToken);

        _bus.RecordOperation(AwsServiceName.Sqs, SqsActionName.ListQueues);
        return Task.FromResult(new ListQueuesResponse
        {
            QueueUrls = items,
            NextToken = nextToken
        }.SetCommonProperties());

        static string TokenGenerator(string x)
        {
            return Convert.ToBase64String(Encoding.UTF8.GetBytes(x));
        }
    }

    /// <summary>Whether a <c>ListQueues</c> NextToken is one it issued: the base64 of a queue URL.</summary>
    private static bool IsQueueUrlToken(string token)
    {
        try
        {
            return Encoding.UTF8.GetString(Convert.FromBase64String(token))
                .StartsWith("http", StringComparison.Ordinal);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    public Task<ListQueueTagsResponse> ListQueueTagsAsync(ListQueueTagsRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var queueName = GetQueueNameFromUrl(request.QueueUrl!);
        if (!Queues.TryGetValue(queueName, out var queue))
        {
            throw new InternalQueueDoesNotExistException();
        }
        Authorize(queue, SqsActionName.ListQueueTags);

        Dictionary<string, string>? tags;
        lock (queue.TagsLock)
        {
            tags = queue.Tags.ToInitializedDictionary();
        }

        _bus.RecordOperation(AwsServiceName.Sqs, SqsActionName.ListQueueTags, queue.Arn);
        return Task.FromResult(new ListQueueTagsResponse
        {
            Tags = tags
        }.SetCommonProperties());
    }

    public Task<PurgeQueueResponse> PurgeQueueAsync(PurgeQueueRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var queueName = GetQueueNameFromUrl(request.QueueUrl!);
        if (!Queues.TryGetValue(queueName, out var queue))
        {
            throw new InternalQueueDoesNotExistException();
        }
        Authorize(queue, SqsActionName.PurgeQueue);

        queue.Messages.Clear();

        var inflightMessageReceipts = queue.InFlightMessages.Keys.ToList();

        foreach (var receipt in inflightMessageReceipts)
        {
            queue.InFlightMessages.TryRemove(receipt, out var inFlightInfo);
            var (_, expirationHandler) = inFlightInfo;
            expirationHandler.Dispose();
        }

        // FIFO messages live in per-group queues rather than the channel.
        queue.MessageGroups.Clear();
        queue.InFlightGroupCounts.Clear();
        queue.DiscardDelayedMessages();
        queue.LatestReceiptHandles.Clear();
        queue.ClearDeduplication();
        queue.ReceiveAttempts.Clear();
        queue.ReceiveAttemptsByReceiptHandle.Clear();

        _bus.RecordOperation(AwsServiceName.Sqs, SqsActionName.PurgeQueue, queue.Arn);
        return Task.FromResult(new PurgeQueueResponse().SetCommonProperties());
    }

    public Task<RemovePermissionResponse> RemovePermissionAsync(RemovePermissionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var queueName = GetQueueNameFromUrl(request.QueueUrl!);
        if (!Queues.TryGetValue(queueName, out var queue))
        {
            throw new InternalQueueDoesNotExistException();
        }
        Authorize(queue, SqsActionName.RemovePermission);

        if (queue.Attributes?.TryGetValue("Policy", out var policyJson) != true)
        {
            throw SqsRequestValidation.InvalidParameterValue("Label", request.Label, "can't find label on existing policy.");
        }

        var policy = JsonNode.Parse(policyJson)!.AsObject();
        var statements = policy["Statement"]!.AsArray();

        int? indexToRemove = null;
        for (int i = 0; i < statements.Count; i++)
        {
            if (statements[i]?["Sid"]?.GetValue<string>() == request.Label)
            {
                indexToRemove = i;
                break;
            }
        }

        if (indexToRemove is null)
        {
            throw SqsRequestValidation.InvalidParameterValue("Label", request.Label, "can't find label on existing policy.");
        }

        statements.RemoveAt(indexToRemove.Value);

        queue.Attributes ??= [];
        if (statements.Count > 0)
        {
            queue.Attributes["Policy"] = policy.ToJsonString();
        }
        else
        {
            queue.Attributes.Remove("Policy");
        }

        _bus.RecordOperation(AwsServiceName.Sqs, SqsActionName.RemovePermission, queue.Arn);
        return Task.FromResult(new RemovePermissionResponse().SetCommonProperties());
    }

    public Task<SendMessageBatchResponse> SendMessageBatchAsync(SendMessageBatchRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var queueName = GetQueueNameFromUrl(request.QueueUrl!);
        if (!Queues.TryGetValue(queueName, out var queue))
        {
            throw new InternalQueueDoesNotExistException();
        }
        Authorize(queue, SqsActionName.SendMessage);

        var response = new SendMessageBatchResponse
        {
            Successful = [],
            Failed = []
        };

        SqsRequestValidation.ValidateBatchRequest(request.Entries, entry => entry.Id, "SendMessageBatchRequestEntry");

        var totalSize = request.Entries!.Sum(e => SqsRequestValidation.MessageSize(e.MessageBody ?? string.Empty, e.MessageAttributes));
        if (totalSize > MaxMessageSize)
        {
            throw new InternalBatchRequestTooLongException(
                $"Batch requests cannot be longer than {MaxMessageSize} bytes. You have sent {totalSize} bytes.");
        }

        EnsureKmsKeyExists(queue);

        // Each entry is validated on its own and fails alone, except that on a FIFO queue an invalid
        // entry refuses the whole request.
        var entryErrors = new AwsServiceException?[request.Entries!.Count];
        for (var i = 0; i < request.Entries.Count; i++)
        {
            var entry = request.Entries[i];
            var entryError = SqsRequestValidation.ValidateSend(queue, new SqsRequestValidation.SendParameters(
                entry.MessageBody, entry.DelaySeconds, entry.MessageGroupId, entry.MessageDeduplicationId,
                entry.MessageAttributes, entry.MessageSystemAttributes), batchIndex: i);
            if (queue.IsFifo && entryError is not null)
            {
                throw entryError.ErrorCode is "EmptyValue" or "MissingParameter"
                    ? SqsRequestValidation.InvalidParameterValue(entryError.Message)
                    : entryError;
            }

            entryErrors[i] = entryError;
        }

        for (var i = 0; i < request.Entries.Count; i++)
        {
            var entry = request.Entries[i];
            if (entryErrors[i] is { } entryError)
            {
                response.Failed.Add(new BatchResultErrorEntry
                {
                    Id = entry.Id,
                    Code = entryError.ErrorCode,
                    Message = entryError.Message,
                    SenderFault = true
                });
                continue;
            }

            var message = CreateMessage(entry.MessageBody!, entry.MessageAttributes, entry.MessageSystemAttributes);

            var reportedMessageId = message.MessageId;

            if (queue.IsFifo)
            {
                // A duplicate is still reported as successful (matching real SQS), but carries the
                // id of the message that originally claimed the deduplication id.
                if (!TryEnqueueFifoMessage(queue, message, entry.MessageGroupId!,
                        entry.MessageDeduplicationId, entry.MessageBody!, _bus.TimeProvider, out var original))
                {
                    reportedMessageId = original.MessageId;
                    message.Attributes!["SequenceNumber"] = original.SequenceNumber!;
                }
            }
            else
            {
                if (entry.MessageGroupId is not null)
                {
                    message.Attributes![InternalMessageSystemAttributeName.MessageGroupId] = entry.MessageGroupId;
                }

                var entryDelaySeconds = entry.DelaySeconds ?? queue.DelaySeconds;
                if (entryDelaySeconds > 0)
                {
                    if (entry.DelaySeconds > 0)
                    {
                        message.Attributes!["DelaySeconds"] = entryDelaySeconds.ToString(NumberFormatInfo.InvariantInfo);
                    }

                    queue.EnqueueAfterDelay(message, TimeSpan.FromSeconds(entryDelaySeconds), _bus.TimeProvider);
                }
                else
                {
                    queue.Messages.Enqueue(message);
                }
            }

            response.Successful.Add(new SendMessageBatchResultEntry
            {
                Id = entry.Id,
                MessageId = reportedMessageId,
                MD5OfMessageBody = message.MD5OfBody,
                MD5OfMessageAttributes = SqsMessageAttributeDigest.Compute(entry.MessageAttributes),
                MD5OfMessageSystemAttributes = SqsMessageAttributeDigest.Compute(entry.MessageSystemAttributes),
                SequenceNumber = message.Attributes?.GetValueOrDefault("SequenceNumber")
            });
        }

        _bus.RecordOperation(AwsServiceName.Sqs, SqsActionName.SendMessageBatch, queue.Arn);
        return Task.FromResult(response.SetCommonProperties());
    }

    public Task<SetQueueAttributesResponse> SetQueueAttributesAsync(SetQueueAttributesRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var queueName = GetQueueNameFromUrl(request.QueueUrl!);
        if (!Queues.TryGetValue(queueName, out var queue))
        {
            throw new InternalQueueDoesNotExistException();
        }
        Authorize(queue, SqsActionName.SetQueueAttributes);

        if (request.Attributes is not { Count: > 0 })
        {
            throw SqsRequestValidation.MissingParameter("Attribute.Name");
        }

        // Every attribute is checked before any is applied: one invalid value applies none.
        queue.Attributes ??= [];
        SqsQueueValidation.ValidateAttributes(request.Attributes, queue.IsFifo, creating: false, current: queue.Attributes);
        SqsQueueValidation.ValidateRedriveAllowPolicyRegion(request.Attributes, queue.Region);
        if (request.Attributes.TryGetValue(InternalQueueAttributeName.RedrivePolicy, out var redrivePolicy))
        {
            ValidateRedrivePolicy(queue, redrivePolicy);
        }

        foreach (var (key, value) in request.Attributes)
        {
            queue.Attributes[key] = value;
        }

        if (request.Attributes.ContainsKey(InternalQueueAttributeName.Policy))
        {
            StorePolicy(queue.Attributes);
        }

        queue.LastModifiedTimestamp = _bus.TimeProvider.GetUtcNow();
        UpdateQueueProperties(queue);
        if (queue.IsFifo)
        {
            // A FIFO queue's delay applies to messages already waiting.
            queue.FifoDelaySecondsChanged(_bus.TimeProvider);
        }

        _bus.RecordOperation(AwsServiceName.Sqs, SqsActionName.SetQueueAttributes, queue.Arn);
        return Task.FromResult(new SetQueueAttributesResponse
        {
            HttpStatusCode = HttpStatusCode.OK
        }.SetCommonProperties());
    }

    private void ExtractRedrivePolicy(SqsQueueResource queue)
    {
        if (queue.Attributes?.TryGetValue(InternalQueueAttributeName.RedrivePolicy, out var emptyPolicy) == true
            && string.IsNullOrEmpty(emptyPolicy))
        {
            // An empty RedrivePolicy unlinks the dead-letter queue.
            queue.Attributes.Remove(InternalQueueAttributeName.RedrivePolicy);
            queue.ErrorQueue = null;
            queue.MaxReceiveCount = null;
            return;
        }

        if (queue.Attributes?.TryGetValue(InternalQueueAttributeName.RedrivePolicy, out var redrivePolicy) == true)
        {
            var policy = JsonDocument.Parse(redrivePolicy);
            var deadLetterTargetArn = policy.RootElement.GetProperty("deadLetterTargetArn").GetString();
            var maxReceiveCount = 0;
            var maxReceiveCountProperty = policy.RootElement.GetProperty("maxReceiveCount");
            if (maxReceiveCountProperty.ValueKind == JsonValueKind.Number)
            {
                maxReceiveCount = maxReceiveCountProperty.GetInt32();
            }
            else
            {
                var maxReceiveCountString = maxReceiveCountProperty.GetString();
                if (maxReceiveCountString != null)
                {
                    maxReceiveCount = int.Parse(maxReceiveCountString, NumberFormatInfo.InvariantInfo);
                }
            }

            if (deadLetterTargetArn != null && maxReceiveCount > 0)
            {
                var deadLetterTargetQueueName = deadLetterTargetArn.Split(':').Last();
                // Checked by ValidateRedrivePolicy when set; the dead-letter queue may have been
                // deleted since, which leaves the policy in place with nowhere to redrive to.
                queue.ErrorQueue = Queues.GetValueOrDefault(deadLetterTargetQueueName);
                queue.MaxReceiveCount = maxReceiveCount;

                // SQS stores the policy normalised: compact JSON with a numeric maxReceiveCount.
                queue.Attributes[InternalQueueAttributeName.RedrivePolicy] = new JsonObject
                {
                    ["deadLetterTargetArn"] = deadLetterTargetArn,
                    ["maxReceiveCount"] = maxReceiveCount
                }.ToJsonString();
            }
        }
    }

    public Task<StartMessageMoveTaskResponse> StartMessageMoveTaskAsync(StartMessageMoveTaskRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var sourceQueueName = GetQueueNameFromArn(request.SourceArn!);
        if (!Queues.TryGetValue(sourceQueueName, out var sourceQueue))
        {
            throw new InternalResourceNotFoundException("Source queue not found.");
        }

        if (_bus.MoveTasks.Values
                .Any(t => t.SourceQueue.Arn.Equals(request.SourceArn, StringComparison.OrdinalIgnoreCase) && t.Status == MoveTaskStatus.Running))
        {
            throw new InternalInvalidParameterValueException(
                "There is already a task running. Only one active task is allowed for a source queue arn at a given time.");
        }

        var deadLetterQueues =
            Queues.Values
                .Select(q => q.ErrorQueue?.Arn)
                .Where(arn => arn is not null);

        if (!deadLetterQueues.Contains(sourceQueue.Arn))
        {
            throw new InternalInvalidParameterValueException("Source queue must be configured as a Dead Letter Queue.");
        }

        if (request.MaxNumberOfMessagesPerSecond is < 1 or > 500)
        {
            throw SqsRequestValidation.InvalidParameterValue(
                "Value for parameter MaxNumberOfMessagesPerSecond is invalid. Reason: You must enter a number that's between 1 and 500.");
        }

        SqsQueueResource? destinationQueue = null;
        if (request.DestinationArn is not null)
        {
            if (string.Equals(request.DestinationArn, request.SourceArn, StringComparison.Ordinal))
            {
                throw SqsRequestValidation.InvalidParameterValue("Source queue arn and destination queue arn cannot be the same.");
            }

            destinationQueue = FindQueueByArn(request.DestinationArn)
                ?? throw new InternalResourceNotFoundException("The resource that you specified for the DestinationArn parameter doesn't exist.");
            if (destinationQueue.IsFifo != sourceQueue.IsFifo)
            {
                throw SqsRequestValidation.InvalidParameterValue("The source queue and destination queue must be of the same queue type.");
            }
        }

        // In-flight messages count too: the task moves them once they become visible again.
        var approximateNumberOfMessages = sourceQueue.Messages.Count + sourceQueue.InFlightMessages.Count;

        var taskHandle = Guid.NewGuid().ToString();
        var moveTask = new SqsMoveTask
        {
            TaskHandle = taskHandle,
            SourceQueue = sourceQueue,
            DestinationQueue = destinationQueue,
            MaxNumberOfMessagesPerSecond = request.MaxNumberOfMessagesPerSecond,
            ApproximateNumberOfMessagesMoved = 0,
            ApproximateNumberOfMessagesToMove = approximateNumberOfMessages,
            MoveTaskJob = new SqsMoveTaskJob(_bus.TimeProvider, sourceQueue, destinationQueue, _bus, request.MaxNumberOfMessagesPerSecond, taskHandle),
            Status = MoveTaskStatus.Running,
            StartedTimestamp = _bus.TimeProvider.GetUtcNow().ToUnixTimeMilliseconds()
        };

        _bus.MoveTasks.TryAdd(moveTask.TaskHandle, moveTask);

        _bus.RecordOperation(AwsServiceName.Sqs, SqsActionName.StartMessageMoveTask, sourceQueue.Arn);
        return Task.FromResult(new StartMessageMoveTaskResponse
        {
            TaskHandle = moveTask.TaskHandle
        }.SetCommonProperties());
    }

    public Task<TagQueueResponse> TagQueueAsync(TagQueueRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var queueName = GetQueueNameFromUrl(request.QueueUrl!);
        if (!Queues.TryGetValue(queueName, out var queue))
        {
            throw new InternalQueueDoesNotExistException();
        }
        Authorize(queue, SqsActionName.TagQueue);

        SqsQueueValidation.ValidateTags(request.Tags);

        lock (queue.TagsLock)
        {
            var newKeys = request.Tags!.Keys.Count(key => queue.Tags?.ContainsKey(key) != true);
            if ((queue.Tags?.Count ?? 0) + newKeys > SqsQueueValidation.MaxTagsPerQueue)
            {
                throw SqsQueueValidation.TooManyTags(queue.Name);
            }

            foreach (var tag in request.Tags)
            {
                queue.Tags ??= [];
                queue.Tags[tag.Key] = tag.Value;
            }
        }

        _bus.RecordOperation(AwsServiceName.Sqs, SqsActionName.TagQueue, queue.Arn);
        return Task.FromResult(new TagQueueResponse().SetCommonProperties());
    }

    public Task<UntagQueueResponse> UntagQueueAsync(UntagQueueRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var queueName = GetQueueNameFromUrl(request.QueueUrl!);
        if (!Queues.TryGetValue(queueName, out var queue))
        {
            throw new InternalQueueDoesNotExistException();
        }
        Authorize(queue, SqsActionName.UntagQueue);

        if (request.TagKeys is not { Count: > 0 })
        {
            throw SqsRequestValidation.MissingParameter("TagKeys");
        }

        foreach (var tagKey in request.TagKeys)
        {
            SqsQueueValidation.ValidateTagKey(tagKey);
        }

        lock (queue.TagsLock)
        {
            foreach (var tagKey in request.TagKeys)
            {
                queue.Tags?.Remove(tagKey);
            }
        }

        _bus.RecordOperation(AwsServiceName.Sqs, SqsActionName.UntagQueue, queue.Arn);
        return Task.FromResult(new UntagQueueResponse().SetCommonProperties());
    }

    public Task<AddPermissionResponse> AddPermissionAsync(AddPermissionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var queueName = GetQueueNameFromUrl(request.QueueUrl!);
        if (!Queues.TryGetValue(queueName, out var queue))
        {
            throw new InternalQueueDoesNotExistException();
        }
        Authorize(queue, SqsActionName.AddPermission);

        SqsQueueValidation.ValidatePermissionActions(request.Actions);

        JsonObject policy;
        if (queue.Attributes?.TryGetValue("Policy", out var policyJson) == true)
        {
            policy = JsonNode.Parse(policyJson)!.AsObject();
        }
        else
        {
            policy = new JsonObject
            {
                ["Version"] = "2012-10-17",
                ["Id"] = $"{queue.Arn}/SQSDefaultPolicy",
                ["Statement"] = new JsonArray()
            };
        }

        var statements = policy["Statement"]!.AsArray();

        foreach (var stmt in statements)
        {
            if (stmt?["Sid"]?.GetValue<string>() == request.Label)
            {
                throw SqsRequestValidation.InvalidParameterValue("Label", request.Label, "Already exists.");
            }
        }

        if (statements.Count >= SqsQueueValidation.MaxPolicyStatements)
        {
            throw SqsRequestValidation.Error("OverLimit",
                $"{statements.Count + 1} Statements were found, maximum allowed is {SqsQueueValidation.MaxPolicyStatements}. ");
        }

        var principals = new JsonArray();
        foreach (var accountId in request.AWSAccountIds ?? [])
        {
            principals.Add((JsonNode?)$"arn:aws:iam::{accountId}:root");
        }

        var actions = new JsonArray();
        foreach (var action in request.Actions ?? [])
        {
            actions.Add((JsonNode?)$"SQS:{action}");
        }

        // SQS writes a single principal or action as a plain string, and several as a list.
        var newStatement = new JsonObject
        {
            ["Sid"] = request.Label,
            ["Effect"] = "Allow",
            ["Principal"] = new JsonObject { ["AWS"] = SingleOrList(principals) },
            ["Action"] = SingleOrList(actions),
            ["Resource"] = queue.Arn
        };

        statements.Add((JsonNode)newStatement);
        queue.Attributes ??= [];
        queue.Attributes["Policy"] = policy.ToJsonString();

        _bus.RecordOperation(AwsServiceName.Sqs, SqsActionName.AddPermission, queue.Arn);
        return Task.FromResult(new AddPermissionResponse().SetCommonProperties());

        static JsonNode SingleOrList(JsonArray values)
        {
            if (values.Count != 1)
            {
                return values;
            }

            var single = values[0]!;
            values.RemoveAt(0);
            return single;
        }
    }

    /// <summary>
    /// Stores a just-written <c>Policy</c> as SQS does: an empty one removes the attribute, and a
    /// bare account-id principal reads back as the account's root ARN.
    /// </summary>
    private static void StorePolicy(Dictionary<string, string> attributes)
    {
        if (!attributes.TryGetValue(InternalQueueAttributeName.Policy, out var policy))
        {
            return;
        }

        if (string.IsNullOrEmpty(policy))
        {
            attributes.Remove(InternalQueueAttributeName.Policy);
        }
        else
        {
            attributes[InternalQueueAttributeName.Policy] = SqsQueuePolicy.Normalize(policy);
        }
    }
}
