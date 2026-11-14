#pragma warning disable CS8600, CS8601, CS8602, CS8604, CS8619 // Nullable reference warnings - internal POCOs use nullable properties but values are set at runtime

using LocalSqsSnsMessaging.Sqs.Model;

namespace LocalSqsSnsMessaging;

/// <summary>Receipt handles, and the operations that take one: DeleteMessage and ChangeMessageVisibility, and their batches.</summary>
internal sealed partial class InternalSqsClient
{
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
}
