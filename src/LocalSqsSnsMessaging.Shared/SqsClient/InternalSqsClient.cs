#pragma warning disable CS8600, CS8601, CS8602, CS8604, CS8619 // Nullable reference warnings - internal POCOs use nullable properties but values are set at runtime

using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using LocalSqsSnsMessaging.Sqs.Model;

namespace LocalSqsSnsMessaging;

internal sealed class InternalSqsClient
{
    private readonly InMemoryAwsBus _bus;

    private const int MaxMessageSize = 1_048_576; // 1MB
    private static readonly string[] InternalAttributes = [
        QueueAttributeName.ApproximateNumberOfMessages,
        QueueAttributeName.ApproximateNumberOfMessagesDelayed,
        QueueAttributeName.ApproximateNumberOfMessagesNotVisible,
        QueueAttributeName.CreatedTimestamp,
        QueueAttributeName.LastModifiedTimestamp,
        QueueAttributeName.QueueArn
    ];

    internal InternalSqsClient(InMemoryAwsBus bus)
    {
        _bus = bus;
    }

    public Task<ChangeMessageVisibilityResponse> ChangeMessageVisibilityAsync(ChangeMessageVisibilityRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var queueName = GetQueueNameFromUrl(request.QueueUrl!);
        if (!_bus.Queues.TryGetValue(queueName, out var queue))
        {
            throw new QueueDoesNotExistException($"Queue {request.QueueUrl} does not exist.");
        }

        if (!IsReceiptHandleValid(request.ReceiptHandle!, queue.Arn))
        {
            throw new ReceiptHandleIsInvalidException($"Receipt handle {request.ReceiptHandle} is invalid.");
        }

        if (queue.InFlightMessages.TryGetValue(request.ReceiptHandle!, out var inFlightInfo))
        {
            var (message, expirationHandler) = inFlightInfo;
            var visibilityTimeout = TimeSpan.FromSeconds(request.VisibilityTimeout.GetValueOrDefault());

            if (visibilityTimeout == TimeSpan.Zero)
            {
                if (queue.InFlightMessages.TryRemove(request.ReceiptHandle!, out var removedInfo))
                {
                    removedInfo.Item2.Dispose();
                    EnqueueMessage(queue, message);
                }
            }
            else
            {
                expirationHandler.UpdateTimeout(visibilityTimeout);
            }

            _bus.RecordOperation(AwsServiceName.Sqs, SqsActionName.ChangeMessageVisibility, queue.Arn);
            return Task.FromResult(new ChangeMessageVisibilityResponse().SetCommonProperties());
        }

        _bus.RecordOperation(AwsServiceName.Sqs, SqsActionName.ChangeMessageVisibility, queue.Arn);
        return Task.FromResult(new ChangeMessageVisibilityResponse().SetCommonProperties());
    }

    private static bool IsReceiptHandleValid(string receiptHandle, string queueArn)
    {
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
        var parts = decoded.Split(' ');
        return parts switch
        {
            [_, var secondItem, _, _] => secondItem.Equals(queueArn, StringComparison.OrdinalIgnoreCase),
            _ => false
        };
    }

    public Task<CreateQueueResponse> CreateQueueAsync(CreateQueueRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var queueUrl = _bus.ServiceUrl is not null
            ? $"{_bus.ServiceUrl.ToString().TrimEnd('/')}/{_bus.CurrentAccountId}/{request.QueueName}"
            : $"https://sqs.{_bus.CurrentRegion}.amazonaws.com/{_bus.CurrentAccountId}/{request.QueueName}";
        var visibilityTimeoutParsed = request.Attributes?.TryGetValue(QueueAttributeName.VisibilityTimeout, out var visibilityTimeout) == true
            ? TimeSpan.FromSeconds(int.Parse(visibilityTimeout, NumberFormatInfo.InvariantInfo))
            : TimeSpan.FromSeconds(30);

        var queue = new SqsQueueResource
        {
            Name = request.QueueName!,
            Region = _bus.CurrentRegion,
            AccountId = _bus.CurrentAccountId,
            Url = queueUrl,
            VisibilityTimeout = visibilityTimeoutParsed,
            Attributes = []
        };

        foreach (var requestAttribute in request.Attributes ?? [])
        {
            if (InternalAttributes.Contains(requestAttribute.Key))
            {
                throw new InvalidOperationException($"Cannot set internal attribute {requestAttribute.Key}");
            }

            queue.Attributes[requestAttribute.Key] = requestAttribute.Value;
        }
        queue.Attributes.Add("QueueArn", queue.Arn);
        UpdateQueueProperties(queue);
        _bus.Queues.TryAdd(request.QueueName!, queue);

        _bus.RecordOperation(AwsServiceName.Sqs, SqsActionName.CreateQueue, queue.Arn);

        var response = new CreateQueueResponse
        {
            QueueUrl = queueUrl
        };

        return Task.FromResult(response.SetCommonProperties());
    }

    public Task<DeleteMessageResponse> DeleteMessageAsync(DeleteMessageRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var queueName = GetQueueNameFromUrl(request.QueueUrl!);
        if (!_bus.Queues.TryGetValue(queueName, out var queue))
        {
            throw new QueueDoesNotExistException($"Queue {request.QueueUrl} does not exist.");
        }

        if (queue.InFlightMessages.TryRemove(request.ReceiptHandle!, out var inFlightInfo))
        {
            var (message, expirationHandler) = inFlightInfo;
            expirationHandler.Dispose();

            if (queue.IsFifo)
            {
                if (message.Attributes!.TryGetValue("MessageGroupId", out var groupId))
                {
                    if (queue.MessageGroups.TryGetValue(groupId, out var groupQueue) && groupQueue.IsEmpty)
                    {
                        queue.MessageGroups.TryRemove(groupId, out _);
                    }
                }

                if (message.Attributes.TryGetValue("MessageDeduplicationId", out var deduplicationId))
                {
                    queue.DeduplicationIds.TryRemove(deduplicationId, out _);
                }
            }

            _bus.RecordOperation(AwsServiceName.Sqs, SqsActionName.DeleteMessage, queue.Arn);
            return Task.FromResult(new DeleteMessageResponse().SetCommonProperties());
        }

        throw new ReceiptHandleIsInvalidException($"Receipt handle {request.ReceiptHandle} is invalid.");
    }

    public Task<DeleteQueueResponse> DeleteQueueAsync(DeleteQueueRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var queueName = GetQueueNameFromUrl(request.QueueUrl!);
        _bus.Queues.TryRemove(queueName, out var removedQueue);
        var queueArn = removedQueue?.Arn ?? $"arn:aws:sqs:{_bus.CurrentRegion}:{_bus.CurrentAccountId}:{queueName}";
        _bus.RecordOperation(AwsServiceName.Sqs, SqsActionName.DeleteQueue, queueArn);
        return Task.FromResult(new DeleteQueueResponse().SetCommonProperties());
    }

    public async Task<ReceiveMessageResponse> ReceiveMessageAsync(ReceiveMessageRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if ((request.MaxNumberOfMessages ?? 1) < 1)
        {
            request.MaxNumberOfMessages = 1;
        }

        var queueName = GetQueueNameFromUrl(request.QueueUrl!);
        if (!_bus.Queues.TryGetValue(queueName, out var queue))
        {
            throw new QueueDoesNotExistException ($"Queue '{queueName}' does not exist.");
        }

        var reader = queue.Messages.Reader;
        List<Message>? messages = null;
        var waitTime = TimeSpan.FromSeconds(request.WaitTimeSeconds.GetValueOrDefault());
        var visibilityTimeout =
            request.VisibilityTimeout.GetValueOrDefault() > 0 ? TimeSpan.FromSeconds(request.VisibilityTimeout.GetValueOrDefault()) : queue.VisibilityTimeout;

        cancellationToken.ThrowIfCancellationRequested();

        if (!queue.IsFifo)
        {
            ReadAvailableMessages();
            if (messages is not null && messages.Count > 0 || waitTime == TimeSpan.Zero)
            {
                _bus.RecordOperation(AwsServiceName.Sqs, SqsActionName.ReceiveMessage, queue.Arn);
                return new ReceiveMessageResponse
                {
                    Messages = messages.ToInitializedList()
                }.SetCommonProperties();
            }

            using var receiveTimeout = _bus.TimeProvider.CreateCancellationTokenSource(waitTime);
            using var linkedToken =
                CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, receiveTimeout.Token);

            try
            {
                await reader.WaitToReadAsync(linkedToken.Token).ConfigureAwait(true);
            }
            catch (OperationCanceledException)
            {
                // This could be due to either the overall timeout or the cancellationToken
            }

            ReadAvailableMessages();
        }
        else
        {
            messages = ReceiveFifoMessages(queue, request.MaxNumberOfMessages ?? 1, visibilityTimeout, request.MessageSystemAttributeNames, cancellationToken);
        }

        _bus.RecordOperation(AwsServiceName.Sqs, SqsActionName.ReceiveMessage, queue.Arn);
        return new ReceiveMessageResponse
        {
            Messages = messages.ToInitializedList(),
        }.SetCommonProperties();

        void ReadAvailableMessages()
        {
            while (reader.TryRead(out var message))
            {
                ReceiveMessageImpl(message, ref messages, queue, visibilityTimeout, request.MessageSystemAttributeNames);
                if (messages is not null && messages.Count >= (request.MaxNumberOfMessages ?? 1))
                {
                    break;
                }
            }
        }
    }

    private void ReceiveMessageImpl(Message message, ref List<Message>? messages, SqsQueueResource queue, TimeSpan visibilityTimeout, List<string>? requestedSystemAttributes)
    {
        if (IsAtMaxReceiveCount(message, queue) && queue.ErrorQueue is not null)
        {
            message.Attributes ??= [];
            message.Attributes[MessageSystemAttributeName.DeadLetterQueueSourceArn] = queue.Arn;
            EnqueueMessage(queue.ErrorQueue, message);
            return;
        }
        IncrementReceiveCount(message);

        var clonedMessage = CloneMessage(message);
        FilterSystemAttributes(clonedMessage, requestedSystemAttributes);
        var receiptHandle = CreateReceiptHandle(message, queue);
        clonedMessage.ReceiptHandle = receiptHandle;
        messages ??= [];
        messages.Add(clonedMessage);

        queue.InFlightMessages[receiptHandle] = (message,
            new SqsInflightMessageExpirationJob(receiptHandle, queue, visibilityTimeout, _bus.TimeProvider));
    }

    private List<Message>? ReceiveFifoMessages(SqsQueueResource queue, int maxMessages, TimeSpan visibilityTimeout, List<string>? requestedSystemAttributes, CancellationToken cancellationToken)
    {
        List<Message>? messages = null;

        foreach (var group in queue.MessageGroups)
        {
            if (messages is not null && messages.Count >= maxMessages)
            {
                break;
            }

            var groupId = group.Key;
            var groupQueue = group.Value;
            var groupLock = queue.MessageGroupLocks.GetOrAdd(groupId, _ => new object());

            lock (groupLock)
            {
                while (groupQueue.TryDequeue(out var message))
                {
                    ReceiveMessageImpl(message, ref messages, queue, visibilityTimeout, requestedSystemAttributes);

                    if (messages is not null && messages.Count >= maxMessages)
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
               queue.Attributes.TryGetValue(QueueAttributeName.DeduplicationScope, out var dedupScope) &&
               dedupScope == "messageGroup" &&
               queue.Attributes.TryGetValue(QueueAttributeName.FifoThroughputLimit, out var throughputLimit) &&
               throughputLimit == "perMessageGroupId";
    }

    private static bool IsAtMaxReceiveCount(Message message, SqsQueueResource queue)
    {
        var receiveCount = message.Attributes?.GetValueOrDefault(MessageSystemAttributeName.ApproximateReceiveCount) ?? "0";
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
        var decodedReceiptHandle = $"{guid} {queue.Arn} {message.MessageId} {_bus.TimeProvider.GetUtcNow().ToUnixTimeMilliseconds() / 1000.0}";
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(decodedReceiptHandle));
    }

    private static void IncrementReceiveCount(Message message)
    {
        var receiveCount = message.Attributes?.GetValueOrDefault(MessageSystemAttributeName.ApproximateReceiveCount) ?? "0";

        var newCount = (int.Parse(receiveCount, NumberFormatInfo.InvariantInfo) + 1).ToString(NumberFormatInfo.InvariantInfo);
        message.Attributes ??= [];
        message.Attributes[MessageSystemAttributeName.ApproximateReceiveCount] = newCount;
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
        if (!_bus.Queues.TryGetValue(queueName, out var queue))
        {
            throw new QueueDoesNotExistException("Queue not found");
        }

        var message = CreateMessage(request.MessageBody!, request.MessageAttributes, request.MessageSystemAttributes);
        var totalSize = CalculateMessageSize(message.Body!, message.MessageAttributes);

        if (totalSize > MaxMessageSize)
        {
            throw new SqsServiceException(
                $"One or more parameters are invalid. Reason: Message must be shorter than {MaxMessageSize} bytes.");
        }

        if (queue.IsFifo)
        {
            if (string.IsNullOrEmpty(request.MessageGroupId))
            {
                throw new InvalidOperationException("MessageGroupId is required for FIFO queues");
            }

            message.Attributes ??= [];
            message.Attributes["MessageGroupId"] = request.MessageGroupId;

            string? deduplicationId = request.MessageDeduplicationId;
            if (string.IsNullOrEmpty(deduplicationId))
            {
                deduplicationId = GenerateMessageBodyHash(request.MessageBody!);
            }

            message.Attributes[MessageSystemAttributeName.MessageDeduplicationId] = deduplicationId;

            bool isFairQueue = IsFairQueue(queue);
            bool isDuplicate;

            if (isFairQueue)
            {
                var groupDeduplicationIds = queue.MessageGroupDeduplicationIds.GetOrAdd(
                    request.MessageGroupId,
                    _ => new ConcurrentDictionary<string, string>());
                isDuplicate = !groupDeduplicationIds.TryAdd(deduplicationId, message.MessageId!);

                if (isDuplicate)
                {
                    return Task.FromResult(new SendMessageResponse
                    {
                        MessageId = groupDeduplicationIds[deduplicationId],
                        MD5OfMessageBody = message.MD5OfBody
                    }.SetCommonProperties());
                }
            }
            else
            {
                isDuplicate = !queue.DeduplicationIds.TryAdd(deduplicationId, message.MessageId!);

                if (isDuplicate)
                {
                    return Task.FromResult(new SendMessageResponse
                    {
                        MessageId = queue.DeduplicationIds[deduplicationId],
                        MD5OfMessageBody = message.MD5OfBody
                    }.SetCommonProperties());
                }
            }

            EnqueueFifoMessage(queue, request.MessageGroupId, message);
        }
        else
        {
            var delaySeconds = request.DelaySeconds.GetValueOrDefault();
            if (delaySeconds > 0)
            {
                message.Attributes ??= [];
                message.Attributes["DelaySeconds"] = delaySeconds.ToString(NumberFormatInfo.InvariantInfo);
                _ = SendDelayedMessageAsync(queue, message, delaySeconds);
            }
            else
            {
                queue.Messages.Writer.TryWrite(message);
            }
        }

        _bus.RecordOperation(AwsServiceName.Sqs, SqsActionName.SendMessage, queue.Arn);
        return Task.FromResult(new SendMessageResponse
        {
            MessageId = message.MessageId,
            MD5OfMessageBody = message.MD5OfBody
        }.SetCommonProperties());
    }

    private static int CalculateMessageSize(string messageBody, Dictionary<string, MessageAttributeValue>? messageAttributes)
    {
        var totalSize = 0;

        totalSize += Encoding.UTF8.GetByteCount(messageBody);

        if (messageAttributes != null)
        {
            foreach (var (key, attributeValue) in messageAttributes)
            {
                totalSize += Encoding.UTF8.GetByteCount(key);
                totalSize += Encoding.UTF8.GetByteCount(attributeValue.DataType!);

                if (attributeValue.BinaryValue != null)
                {
                    totalSize += (int)attributeValue.BinaryValue.Length;
                }
                else if (attributeValue.StringValue != null)
                {
                    totalSize += Encoding.UTF8.GetByteCount(attributeValue.StringValue);
                }
            }
        }

        return totalSize;
    }

    private static void EnqueueMessage(SqsQueueResource targetQueue, Message message)
    {
        if (targetQueue.IsFifo)
        {
            if (!message.Attributes!.TryGetValue("MessageGroupId", out var groupId) || string.IsNullOrEmpty(groupId))
            {
                throw new InvalidOperationException("Message destined for a FIFO queue must have a MessageGroupId.");
            }
            EnqueueFifoMessage(targetQueue, groupId, message);
        }
        else
        {
            targetQueue.Messages.Writer.TryWrite(message);
        }
    }

    private static void EnqueueFifoMessage(SqsQueueResource queue, string messageGroupId, Message message)
    {
        var groupLock = queue.MessageGroupLocks.GetOrAdd(messageGroupId, _ => new object());
        lock (groupLock)
        {
            queue.MessageGroups.AddOrUpdate(messageGroupId,
                _ => new ConcurrentQueue<Message>([message]),
                (_, existingQueue) =>
                {
                    existingQueue.Enqueue(message);
                    return existingQueue;
                });
        }
    }

    private static string GenerateMessageBodyHash(string messageBody)
    {
        var hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(messageBody));
        return Convert.ToBase64String(hashBytes);
    }

    private static Message CreateMessage(string messageBody, Dictionary<string, MessageAttributeValue>? messageAttributes, Dictionary<string, MessageSystemAttributeValue>? messageSystemAttributes)
    {
        var message = new Message
        {
            MessageId = Guid.NewGuid().ToString(),
            Body = messageBody,
            MessageAttributes = messageAttributes,
            Attributes = messageSystemAttributes?.ToDictionary(kv => kv.Key, kv => kv.Value.StringValue)
        };

#pragma warning disable CA5351
        var hash = MD5.HashData(Encoding.UTF8.GetBytes(messageBody));
#pragma warning restore CA5351
#pragma warning disable CA1308
        message.MD5OfBody = Convert.ToHexString(hash).ToLowerInvariant();
#pragma warning restore CA1308

        return message;
    }

    private async Task SendDelayedMessageAsync(SqsQueueResource queue, Message message, int delaySeconds)
    {
        await _bus.TimeProvider.Delay(TimeSpan.FromSeconds(delaySeconds)).ConfigureAwait(true);
        queue.Messages.Writer.TryWrite(message);
    }

    public Task<CancelMessageMoveTaskResponse> CancelMessageMoveTaskAsync(CancelMessageMoveTaskRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!_bus.MoveTasks.TryGetValue(request.TaskHandle!, out var task))
        {
            throw new ResourceNotFoundException("Task does not exist.");
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
        if (!_bus.Queues.TryGetValue(queueName, out var queue))
        {
            throw new QueueDoesNotExistException($"Queue {request.QueueUrl} does not exist.");
        }

        var response = new ChangeMessageVisibilityBatchResponse
        {
            Successful = [],
            Failed = []
        };

        foreach (var entry in request.Entries!)
        {
            try
            {
                if (queue.InFlightMessages.TryGetValue(entry.ReceiptHandle!, out var message))
                {
                    var (_, inFlightExpireCallback) = message;
                    inFlightExpireCallback.UpdateTimeout(TimeSpan.FromSeconds(entry.VisibilityTimeout.GetValueOrDefault()));

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
                        Code = "ReceiptHandleIsInvalid",
                        Message = $"Receipt handle {entry.ReceiptHandle} is invalid.",
                        SenderFault = true
                    });
                }
            }
#pragma warning disable CA1031
            catch (Exception ex)
#pragma warning restore CA1031
            {
                response.Failed.Add(new BatchResultErrorEntry
                {
                    Id = entry.Id,
                    Code = "InternalError",
                    Message = ex.Message,
                    SenderFault = false
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
        if (!_bus.Queues.TryGetValue(queueName, out var queue))
        {
            throw new QueueDoesNotExistException($"Queue {request.QueueUrl} does not exist.");
        }

        var response = new DeleteMessageBatchResponse
        {
            Successful = [],
            Failed = []
        };

        foreach (var entry in request.Entries!)
        {
            try
            {
                if (queue.InFlightMessages.TryRemove(entry.ReceiptHandle!, out _))
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
                        Code = "ReceiptHandleIsInvalid",
                        Message = $"Receipt handle {entry.ReceiptHandle} is invalid.",
                        SenderFault = true
                    });
                }
            }
#pragma warning disable CA1031
            catch (Exception ex)
#pragma warning restore CA1031
            {
                response.Failed.Add(new BatchResultErrorEntry
                {
                    Id = entry.Id,
                    Code = "InternalError",
                    Message = ex.Message,
                    SenderFault = false
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
        if (!_bus.Queues.TryGetValue(queueName, out var queue))
        {
            throw new QueueDoesNotExistException($"Queue {request.QueueUrl} does not exist.");
        }

        var attributes = new Dictionary<string, string>();
        var attributeNames = request.AttributeNames ?? [];

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
        if (queue.Attributes?.TryGetValue(QueueAttributeName.VisibilityTimeout, out var visibilityTimeout) == true)
        {
            queue.VisibilityTimeout = TimeSpan.FromSeconds(int.Parse(visibilityTimeout, NumberFormatInfo.InvariantInfo));
        }

        ExtractRedrivePolicy(queue);
    }

    private static bool IsComputedAttribute(string attributeName)
    {
        return attributeName == QueueAttributeName.ApproximateNumberOfMessages
               || attributeName == QueueAttributeName.ApproximateNumberOfMessagesNotVisible
               || attributeName == QueueAttributeName.ApproximateNumberOfMessagesDelayed;
    }

    private static void AddComputedAttributes(SqsQueueResource queue, Dictionary<string, string> attributes)
    {
        attributes[QueueAttributeName.ApproximateNumberOfMessages] = queue.Messages.Reader.Count.ToString(NumberFormatInfo.InvariantInfo);
        attributes[QueueAttributeName.ApproximateNumberOfMessagesNotVisible] = queue.InFlightMessages.Count.ToString(NumberFormatInfo.InvariantInfo);
        attributes[QueueAttributeName.ApproximateNumberOfMessagesDelayed] = "0";
    }

    private static void AddComputedAttribute(SqsQueueResource queue, string attributeName, Dictionary<string, string> attributes)
    {
        if (attributeName == QueueAttributeName.ApproximateNumberOfMessages)
        {
            attributes[attributeName] = queue.Messages.Reader.Count.ToString(NumberFormatInfo.InvariantInfo);
        }
        else if (attributeName == QueueAttributeName.ApproximateNumberOfMessagesNotVisible)
        {
            attributes[attributeName] = queue.InFlightMessages.Count.ToString(NumberFormatInfo.InvariantInfo);
        }
        else if (attributeName == QueueAttributeName.ApproximateNumberOfMessagesDelayed)
        {
            attributes[attributeName] = "0";
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

        if (!_bus.Queues.TryGetValue(request.QueueName!, out var queue))
        {
            throw new QueueDoesNotExistException($"Queue {request.QueueName} does not exist.");
        }
        _bus.RecordOperation(AwsServiceName.Sqs, SqsActionName.GetQueueUrl, queue.Arn);
        return Task.FromResult(new GetQueueUrlResponse { QueueUrl = queue.Url }.SetCommonProperties());
    }

    public Task<ListDeadLetterSourceQueuesResponse> ListDeadLetterSourceQueuesAsync(
        ListDeadLetterSourceQueuesRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var deadLetterQueueArn = request.QueueUrl;
        var deadLetterSourceQueues = _bus.Queues.Values
            .Where(q => q.ErrorQueue?.Arn == deadLetterQueueArn)
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

        var tasks = _bus.MoveTasks.Values
            .Where(t => t.SourceQueue.Arn.Equals(request.SourceArn, StringComparison.OrdinalIgnoreCase))
            .Select(t => new ListMessageMoveTasksResultEntry
            {
                TaskHandle = t.TaskHandle,
                SourceArn = t.SourceQueue.Arn,
                DestinationArn = t.DestinationQueue?.Arn,
                MaxNumberOfMessagesPerSecond = t.MaxNumberOfMessagesPerSecond,
                Status = MoveTaskStatus.Running,
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

        var allQueues = _bus.Queues
            .Values
            .Where(q => string.IsNullOrEmpty(request.QueueNamePrefix) || q.Name.StartsWith(request.QueueNamePrefix, StringComparison.OrdinalIgnoreCase))
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

    public Task<ListQueueTagsResponse> ListQueueTagsAsync(ListQueueTagsRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var queueName = GetQueueNameFromUrl(request.QueueUrl!);
        if (!_bus.Queues.TryGetValue(queueName, out var queue))
        {
            throw new QueueDoesNotExistException("Queue not found.");
        }

        _bus.RecordOperation(AwsServiceName.Sqs, SqsActionName.ListQueueTags, queue.Arn);
        return Task.FromResult(new ListQueueTagsResponse
        {
            Tags = queue.Tags.ToInitializedDictionary()
        }.SetCommonProperties());
    }

    public Task<PurgeQueueResponse> PurgeQueueAsync(PurgeQueueRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var queueName = GetQueueNameFromUrl(request.QueueUrl!);
        if (!_bus.Queues.TryGetValue(queueName, out var queue))
        {
            throw new QueueDoesNotExistException($"Queue {request.QueueUrl} does not exist.");
        }

        while (queue.Messages.Reader.TryRead(out _))
        {
        }

        var inflightMessageReceipts = queue.InFlightMessages.Keys.ToList();

        foreach (var receipt in inflightMessageReceipts)
        {
            queue.InFlightMessages.TryRemove(receipt, out var inFlightInfo);
            var (_, expirationHandler) = inFlightInfo;
            expirationHandler.Dispose();
        }

        _bus.RecordOperation(AwsServiceName.Sqs, SqsActionName.PurgeQueue, queue.Arn);
        return Task.FromResult(new PurgeQueueResponse().SetCommonProperties());
    }

    public Task<RemovePermissionResponse> RemovePermissionAsync(RemovePermissionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var queueName = GetQueueNameFromUrl(request.QueueUrl!);
        if (!_bus.Queues.TryGetValue(queueName, out var queue))
        {
            throw new QueueDoesNotExistException($"Queue {request.QueueUrl} does not exist.");
        }

        if (queue.Attributes?.TryGetValue("Policy", out var policyJson) != true)
        {
            throw new ArgumentException($"Value {request.Label} for parameter Label is invalid. Reason: can't find label.");
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
            throw new ArgumentException($"Value {request.Label} for parameter Label is invalid. Reason: can't find label.");
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
        if (!_bus.Queues.TryGetValue(queueName, out var queue))
        {
            throw new QueueDoesNotExistException($"Queue {request.QueueUrl} does not exist.");
        }

        var response = new SendMessageBatchResponse
        {
            Successful = [],
            Failed = []
        };

        var totalSize = request.Entries!.Sum(e => CalculateMessageSize(e.MessageBody!, e.MessageAttributes));

        if (totalSize > MaxMessageSize)
        {
            throw new BatchRequestTooLongException(
                $"Batch size ({totalSize} bytes) exceeds the maximum allowed size ({MaxMessageSize} bytes)");
        }

        foreach (var entry in request.Entries)
        {
            var message = CreateMessage(entry.MessageBody!, entry.MessageAttributes, entry.MessageSystemAttributes);

            var entryDelaySeconds = entry.DelaySeconds.GetValueOrDefault();
            if (entryDelaySeconds > 0)
            {
                message.Attributes!["DelaySeconds"] = entryDelaySeconds.ToString(NumberFormatInfo.InvariantInfo);
                _ = SendDelayedMessageAsync(queue, message, entryDelaySeconds);
            }
            else
            {
                queue.Messages.Writer.TryWrite(message);
            }

            response.Successful.Add(new SendMessageBatchResultEntry
            {
                Id = entry.Id,
                MessageId = message.MessageId,
                MD5OfMessageBody = message.MD5OfBody
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
        if (!_bus.Queues.TryGetValue(queueName, out var queue))
        {
            throw new QueueDoesNotExistException($"Queue {request.QueueUrl} does not exist.");
        }

        queue.Attributes ??= [];
        foreach (var (key, value) in request.Attributes ?? [])
        {
            queue.Attributes[key] = value;
        }

        UpdateQueueProperties(queue);

        _bus.RecordOperation(AwsServiceName.Sqs, SqsActionName.SetQueueAttributes, queue.Arn);
        return Task.FromResult(new SetQueueAttributesResponse
        {
            HttpStatusCode = HttpStatusCode.OK
        }.SetCommonProperties());
    }

    private void ExtractRedrivePolicy(SqsQueueResource queue)
    {
        if (queue.Attributes?.TryGetValue(QueueAttributeName.RedrivePolicy, out var redrivePolicy) == true)
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
                if (!_bus.Queues.TryGetValue(deadLetterTargetQueueName, out var errorQueue))
                {
                    throw new InvalidOperationException("Dead letter queue not found");
                }

                queue.ErrorQueue = errorQueue;
                queue.MaxReceiveCount = maxReceiveCount;
            }
        }
    }

    public Task<StartMessageMoveTaskResponse> StartMessageMoveTaskAsync(StartMessageMoveTaskRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var sourceQueueName = GetQueueNameFromArn(request.SourceArn!);
        if (!_bus.Queues.TryGetValue(sourceQueueName, out var sourceQueue))
        {
            throw new ResourceNotFoundException("Source queue not found.");
        }

        if (_bus.MoveTasks.Values
                .Any(t => t.SourceQueue.Arn.Equals(request.SourceArn, StringComparison.OrdinalIgnoreCase) && t.Status == MoveTaskStatus.Running))
        {
            throw new UnsupportedOperationException("Move task already running for the source queue.");
        }

        var deadLetterQueues =
            _bus.Queues.Values
                .Select(q => q.ErrorQueue?.Arn)
                .Where(arn => arn is not null);

        if (!deadLetterQueues.Contains(sourceQueue.Arn))
        {
            throw new InvalidOperationException("Source queue is not a dead letter queue.");
        }

        SqsQueueResource? destinationQueue = null;
        if (request.DestinationArn is not null)
        {
            var destinationQueueName = GetQueueNameFromArn(request.DestinationArn);
            if (!_bus.Queues.TryGetValue(destinationQueueName, out destinationQueue))
            {
                throw new ResourceNotFoundException("Destination queue not found.")
                {
                    ErrorCode = "ResourceNotFoundException",
                    StatusCode = HttpStatusCode.BadRequest
                };
            }
        }

        var approximateNumberOfMessages =
            sourceQueue.Attributes?.GetValueOrDefault(QueueAttributeName.ApproximateNumberOfMessages) ?? "0";

        var moveTask = new SqsMoveTask
        {
            TaskHandle = Guid.NewGuid().ToString(),
            SourceQueue = sourceQueue,
            DestinationQueue = destinationQueue,
            MaxNumberOfMessagesPerSecond = request.MaxNumberOfMessagesPerSecond,
            ApproximateNumberOfMessagesMoved = 0,
            ApproximateNumberOfMessagesToMove = int.Parse(approximateNumberOfMessages, NumberFormatInfo.InvariantInfo),
            MoveTaskJob = new SqsMoveTaskJob(_bus.TimeProvider, sourceQueue, destinationQueue, _bus, request.MaxNumberOfMessagesPerSecond),
            Status = MoveTaskStatus.Running
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
        if (!_bus.Queues.TryGetValue(queueName, out var queue))
        {
            throw new QueueDoesNotExistException("Queue not found.");
        }

        foreach (var tag in request.Tags ?? [])
        {
            if (tag.Value is not null)
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
        if (!_bus.Queues.TryGetValue(queueName, out var queue))
        {
            throw new QueueDoesNotExistException("Queue not found.");
        }

        foreach (var tagKey in request.TagKeys ?? [])
        {
            queue.Tags?.Remove(tagKey);
        }

        _bus.RecordOperation(AwsServiceName.Sqs, SqsActionName.UntagQueue, queue.Arn);
        return Task.FromResult(new UntagQueueResponse().SetCommonProperties());
    }

    public Task<AddPermissionResponse> AddPermissionAsync(AddPermissionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var queueName = GetQueueNameFromUrl(request.QueueUrl!);
        if (!_bus.Queues.TryGetValue(queueName, out var queue))
        {
            throw new QueueDoesNotExistException("Queue not found.");
        }

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
                throw new ArgumentException($"Value {request.Label} for parameter Label is invalid. Reason: Already exists.");
            }
        }

        var principals = new JsonArray();
        foreach (var accountId in request.AWSAccountIds ?? [])
        {
            principals.Add($"arn:aws:iam::{accountId}:root");
        }

        var actions = new JsonArray();
        foreach (var action in request.Actions ?? [])
        {
            actions.Add($"SQS:{action}");
        }

        var newStatement = new JsonObject
        {
            ["Sid"] = request.Label,
            ["Effect"] = "Allow",
            ["Principal"] = new JsonObject { ["AWS"] = principals },
            ["Action"] = actions,
            ["Resource"] = queue.Arn
        };

        statements.Add(newStatement);
        queue.Attributes ??= [];
        queue.Attributes["Policy"] = policy.ToJsonString();

        _bus.RecordOperation(AwsServiceName.Sqs, SqsActionName.AddPermission, queue.Arn);
        return Task.FromResult(new AddPermissionResponse().SetCommonProperties());
    }
}
