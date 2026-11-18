#pragma warning disable CS8600, CS8601, CS8602, CS8604, CS8619 // Nullable reference warnings - internal POCOs use nullable properties but values are set at runtime

using System.Security.Cryptography;
using InMemoryAws.Sqs.Model;

namespace InMemoryAws;

/// <summary>SendMessage and SendMessageBatch.</summary>
internal sealed partial class InternalSqsClient
{
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

        if (!queue.AcceptSent(message, request.MessageGroupId, request.MessageDeduplicationId, request.DelaySeconds,
                _cloud.TimeProvider, out var original))
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

        _cloud.RecordOperation(AwsServiceName.Sqs, SqsActionName.SendMessage, queue.Arn);
        return Task.FromResult(new SendMessageResponse
        {
            MessageId = message.MessageId,
            MD5OfMessageBody = message.MD5OfBody,
            MD5OfMessageAttributes = SqsMessageAttributeDigest.Compute(request.MessageAttributes),
            MD5OfMessageSystemAttributes = SqsMessageAttributeDigest.Compute(request.MessageSystemAttributes),
            SequenceNumber = message.Attributes?.GetValueOrDefault("SequenceNumber")
        }.SetCommonProperties());
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

            // A FIFO duplicate is still reported as successful (matching real SQS), but carries the
            // id of the message that originally claimed the deduplication id.
            if (!queue.AcceptSent(message, entry.MessageGroupId, entry.MessageDeduplicationId, entry.DelaySeconds,
                    _cloud.TimeProvider, out var original))
            {
                reportedMessageId = original.MessageId;
                message.Attributes!["SequenceNumber"] = original.SequenceNumber!;
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

        _cloud.RecordOperation(AwsServiceName.Sqs, SqsActionName.SendMessageBatch, queue.Arn);
        return Task.FromResult(response.SetCommonProperties());
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

        // SenderId is the caller's IAM user id (see SqsCaller.SenderId); the raw client and
        // unsigned requests are attributed to the cloud's CurrentAccountId. The queue stamps
        // SentTimestamp when it accepts the message (SqsQueueResource.AcceptSent).
        attributes ??= [];
        attributes[InternalMessageSystemAttributeName.SenderId] = (Caller ?? SqsCaller.Unsigned).SenderId(_cloud);

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
}
