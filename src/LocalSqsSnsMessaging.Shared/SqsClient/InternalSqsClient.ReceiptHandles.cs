#pragma warning disable CS8600, CS8601, CS8602, CS8604, CS8619 // Nullable reference warnings - internal POCOs use nullable properties but values are set at runtime

using LocalSqsSnsMessaging.Sqs.Model;

namespace LocalSqsSnsMessaging;

/// <summary>Receipt handles, and the operations that take one: DeleteMessage and ChangeMessageVisibility, and their batches.</summary>
internal sealed partial class InternalSqsClient
{
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

    /// <summary>Deletes a message by receipt handle (see <see cref="SqsQueueResource.DeleteMessage"/>).</summary>
    /// <returns>The error to report, or <see langword="null"/> on success.</returns>
    private static AwsServiceException? DeleteByReceiptHandle(SqsQueueResource queue, string? receiptHandle) =>
        ReceiptHandleError(queue.DeleteMessage(receiptHandle), receiptHandle, visibilityTimeoutSeconds: 0);

    /// <summary>Changes a message's visibility by receipt handle (see <see cref="SqsQueueResource.ChangeVisibility"/>).</summary>
    /// <returns>The error to report, or <see langword="null"/> on success.</returns>
    private static AwsServiceException? ChangeVisibilityByReceiptHandle(SqsQueueResource queue, string? receiptHandle, int visibilityTimeoutSeconds)
    {
        try
        {
            SqsRequestValidation.ValidateVisibilityTimeout(visibilityTimeoutSeconds);
        }
        catch (AwsServiceException error)
        {
            return error;
        }

        return ReceiptHandleError(queue.ChangeVisibility(receiptHandle, TimeSpan.FromSeconds(visibilityTimeoutSeconds)),
            receiptHandle, visibilityTimeoutSeconds);
    }

    /// <summary>The error SQS answers a receipt handle's outcome with; <see langword="null"/> for success.</summary>
    private static AwsServiceException? ReceiptHandleError(SqsReceiptHandleOutcome outcome, string? receiptHandle, int visibilityTimeoutSeconds) => outcome switch
    {
        SqsReceiptHandleOutcome.Done => null,
        SqsReceiptHandleOutcome.Expired => new InternalInvalidParameterValueException(
            $"Value {receiptHandle} for parameter ReceiptHandle is invalid. Reason: The receipt handle has expired."),
        SqsReceiptHandleOutcome.MessageNotAvailable => new InternalInvalidParameterValueException(
            $"Value {receiptHandle} for parameter ReceiptHandle is invalid. Reason: Message does not exist or is not available for visibility timeout change."),
        SqsReceiptHandleOutcome.VisibilityCapExceeded => SqsRequestValidation.VisibilityCapExceeded(visibilityTimeoutSeconds),
        // The queue the handle was issued by no longer exists, whatever now has its name.
        SqsReceiptHandleOutcome.QueueDeleted => new InternalQueueDoesNotExistException(),
        SqsReceiptHandleOutcome.NotThisQueue => new InternalReceiptHandleIsInvalidException($"The receipt handle \"{receiptHandle}\" is not valid for this queue."),
        _ => new InternalReceiptHandleIsInvalidException("The input receipt handle is invalid."),
    };
}
