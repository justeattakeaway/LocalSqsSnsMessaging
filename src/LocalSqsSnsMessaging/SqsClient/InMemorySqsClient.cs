using System.Collections.Concurrent;
using System.Net;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;
using Amazon.Auth.AccessControlPolicy;
using Amazon.Runtime;
using Amazon.SQS;
using Amazon.SQS.Model;
using BatchResultErrorEntry = Amazon.SQS.Model.BatchResultErrorEntry;
using MessageAttributeValue = Amazon.SQS.Model.MessageAttributeValue;
using RemovePermissionRequest = Amazon.SQS.Model.RemovePermissionRequest;
using RemovePermissionResponse = Amazon.SQS.Model.RemovePermissionResponse;
using ResourceNotFoundException = Amazon.SQS.Model.ResourceNotFoundException;

namespace LocalSqsSnsMessaging;

/// <summary>
/// Represents an in-memory implementation of Amazon Simple Queue Service (SQS) client.
/// This class provides methods to interact with SQS queues in a local, in-memory environment,
/// primarily for testing and development purposes without connecting to actual AWS services.
/// It implements the IAmazonSQS interface to maintain compatibility with the AWS SDK.
/// </summary>
public sealed partial class InMemorySqsClient : IAmazonSQS
{
    private readonly InMemoryAwsBus _bus;
    private readonly Lazy<ISQSPaginatorFactory> _paginators;

    private const int MaxMessageSize = 1_048_576; // 1MB
    private static readonly string[] InternalAttributes = [
        QueueAttributeName.ApproximateNumberOfMessages,
        QueueAttributeName.ApproximateNumberOfMessagesDelayed,
        QueueAttributeName.ApproximateNumberOfMessagesNotVisible,
        QueueAttributeName.CreatedTimestamp,
        QueueAttributeName.LastModifiedTimestamp,
        QueueAttributeName.QueueArn
    ];

    internal InMemorySqsClient(InMemoryAwsBus bus)
    {
        _bus = bus;
        _paginators = new(() => GetPaginatorFactory(this));
    }

#pragma warning disable CA1063
    void IDisposable.Dispose()
#pragma warning restore CA1063
    {
    }

    IClientConfig? IAmazonService.Config => null;

    public Task<Dictionary<string, string>> GetAttributesAsync(string queueUrl)
    {
        ArgumentNullException.ThrowIfNull(queueUrl);

        var queueName = GetQueueNameFromUrl(queueUrl);
        if (_bus.Queues.TryGetValue(queueName, out var queue))
        {
            return Task.FromResult(queue.Attributes ?? []);
        }

        throw new QueueDoesNotExistException($"Queue {queueUrl} does not exist.");
    }

    public Task SetAttributesAsync(string queueUrl, Dictionary<string, string> attributes)
    {
        ArgumentNullException.ThrowIfNull(queueUrl);
        ArgumentNullException.ThrowIfNull(attributes);

        var queueName = GetQueueNameFromUrl(queueUrl);
        if (!_bus.Queues.TryGetValue(queueName, out var queue))
        {
            throw new QueueDoesNotExistException($"Queue {queueName} does not exist.");
        }

        foreach (var (key, value) in attributes)
        {
            if (InternalAttributes.Contains(key))
            {
                throw new InvalidOperationException($"Cannot set internal attribute {key}");
            }

            queue.Attributes ??= [];
            queue.Attributes[key] = value;
        }

        return Task.CompletedTask;
    }

    public Task<ChangeMessageVisibilityResponse> ChangeMessageVisibilityAsync(ChangeMessageVisibilityRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var queueName = GetQueueNameFromUrl(request.QueueUrl);
        if (!_bus.Queues.TryGetValue(queueName, out var queue))
        {
            throw new QueueDoesNotExistException($"Queue {request.QueueUrl} does not exist.");
        }

        if (!IsReceiptHandleValid(request.ReceiptHandle, queue.Arn))
        {
            throw new ReceiptHandleIsInvalidException($"Receipt handle {request.ReceiptHandle} is invalid.");
        }

        if (queue.InFlightMessages.TryGetValue(request.ReceiptHandle, out var inFlightInfo))
        {
            var (message, expirationHandler) = inFlightInfo;
            var visibilityTimeout = TimeSpan.FromSeconds(request.VisibilityTimeout.GetValueOrDefault());

            if (visibilityTimeout == TimeSpan.Zero)
            {
                // Atomically remove from in-flight and re-queue if successful.
                if (queue.InFlightMessages.Remove(request.ReceiptHandle, out var removedInfo))
                {
                    removedInfo.Item2.Dispose(); // Dispose the expiration job

                    // Re-queue the message to the correct location using the new abstracted method.
                    EnqueueMessage(queue, message);
                }
            }
            else
            {
                // For non-zero timeouts, just update the expiration.
                expirationHandler.UpdateTimeout(visibilityTimeout);
            }

            return Task.FromResult(new ChangeMessageVisibilityResponse().SetCommonProperties());
        }

        // If the message is not in-flight, it might have already been processed or expired.
        // SQS does not throw an error in this case, so we return a success response.
        return Task.FromResult(new ChangeMessageVisibilityResponse().SetCommonProperties());
    }

    private static bool IsReceiptHandleValid(string receiptHandle, string queueArn)
    {
        var bufferLength = receiptHandle.Length * 3 / 4;
        var buffer = bufferLength <= 1024 ? stackalloc byte[bufferLength] : new byte[bufferLength];
        if (!Convert.TryFromBase64String(receiptHandle, buffer, out var written))
        {
            return false;
        }
        var decoded = Encoding.UTF8.GetString(buffer[..written]);
        var parts = decoded.Split(' ');
        return parts switch
        {
            [_, var secondItem, _, _] => secondItem.Equals(queueArn, StringComparison.OrdinalIgnoreCase),
            _ => false
        };
    }

    public Task<CreateQueueResponse> CreateQueueAsync(string queueName,
        CancellationToken cancellationToken = default)
    {
        return CreateQueueAsync(new CreateQueueRequest
        {
            QueueName = queueName,
        }, cancellationToken);
    }

    public Task<CreateQueueResponse> CreateQueueAsync(CreateQueueRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var queueUrl = $"https://sqs.{_bus.CurrentRegion}.amazonaws.com/{_bus.CurrentAccountId}/{request.QueueName}";
        var visibilityTimeoutParsed = request.Attributes?.TryGetValue(QueueAttributeName.VisibilityTimeout, out var visibilityTimeout) == true
            ? TimeSpan.FromSeconds(int.Parse(visibilityTimeout, NumberFormatInfo.InvariantInfo))
            : TimeSpan.FromSeconds(30);

        var queue = new SqsQueueResource
        {
            Name = request.QueueName,
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
        _bus.Queues.TryAdd(request.QueueName, queue);

        var response = new CreateQueueResponse
        {
            QueueUrl = queueUrl
        };

        return Task.FromResult(response.SetCommonProperties());
    }

    public Task<DeleteMessageResponse> DeleteMessageAsync(string queueUrl, string receiptHandle,
        CancellationToken cancellationToken = default)
    {
        return DeleteMessageAsync(new DeleteMessageRequest
        {
            QueueUrl = queueUrl,
            ReceiptHandle = receiptHandle
        }, cancellationToken);
    }

    public Task<DeleteMessageResponse> DeleteMessageAsync(DeleteMessageRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var queueName = GetQueueNameFromUrl(request.QueueUrl);
        if (!_bus.Queues.TryGetValue(queueName, out var queue))
        {
            throw new QueueDoesNotExistException($"Queue {request.QueueUrl} does not exist.");
        }

        if (queue.InFlightMessages.Remove(request.ReceiptHandle, out var inFlightInfo))
        {
            var (message, expirationHandler) = inFlightInfo;
            expirationHandler.Dispose();

            if (queue.IsFifo)
            {
                // Remove the message from the MessageGroups if it's the last one in its group
                if (message.Attributes.TryGetValue("MessageGroupId", out var groupId))
                {
                    if (queue.MessageGroups.TryGetValue(groupId, out var groupQueue) && groupQueue.IsEmpty)
                    {
                        queue.MessageGroups.TryRemove(groupId, out _);
                    }
                }

                // Remove the deduplication ID if it exists
                if (message.Attributes.TryGetValue("MessageDeduplicationId", out var deduplicationId))
                {
                    queue.DeduplicationIds.TryRemove(deduplicationId, out _);
                }
            }

            return Task.FromResult(new DeleteMessageResponse().SetCommonProperties());
        }

        throw new ReceiptHandleIsInvalidException($"Receipt handle {request.ReceiptHandle} is invalid.");
    }

    public Task<DeleteQueueResponse> DeleteQueueAsync(string queueUrl, CancellationToken cancellationToken = default)
    {
        return DeleteQueueAsync(new DeleteQueueRequest
        {
            QueueUrl = queueUrl
        }, cancellationToken);
    }

    public Task<GetQueueUrlResponse> GetQueueUrlAsync(string queueName, CancellationToken cancellationToken = default)
    {
        return GetQueueUrlAsync(new GetQueueUrlRequest
        {
            QueueName = queueName
        }, cancellationToken);
    }

    public Task<ListQueuesResponse> ListQueuesAsync(string queueNamePrefix,
        CancellationToken cancellationToken = default)
    {
        return ListQueuesAsync(new ListQueuesRequest
        {
            QueueNamePrefix = queueNamePrefix
        }, cancellationToken);
    }

    public Task<ReceiveMessageResponse> ReceiveMessageAsync(string queueUrl,
        CancellationToken cancellationToken = default)
    {
        return ReceiveMessageAsync(new ReceiveMessageRequest
        {
            QueueUrl = queueUrl,
            MaxNumberOfMessages = 1,
            WaitTimeSeconds = 0
        }, cancellationToken);
    }

    public async Task<ReceiveMessageResponse> ReceiveMessageAsync(ReceiveMessageRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.MaxNumberOfMessages.GetValueOrDefault(1) < 1)
        {
            request.MaxNumberOfMessages = 1;
        }

        var queueName = GetQueueNameFromUrl(request.QueueUrl);
        if (!_bus.Queues.TryGetValue(queueName, out var queue))
        {
            throw new QueueDoesNotExistException ($"Queue '{queueName}' does not exist.");
        }

        var reader = queue.Messages.Reader;
        List<Message>? messages = null;
        var waitTime = TimeSpan.FromSeconds(request.WaitTimeSeconds.GetValueOrDefault());
        var visibilityTimeout =
            request.VisibilityTimeout > 0 ? TimeSpan.FromSeconds(request.VisibilityTimeout.GetValueOrDefault()) : queue.VisibilityTimeout;

        cancellationToken.ThrowIfCancellationRequested();

        if (!queue.IsFifo)
        {
            ReadAvailableMessages();
            if (messages is not null && messages.Count > 0 || waitTime == TimeSpan.Zero)
            {
                return new ReceiveMessageResponse
                {
                    Messages = messages.ToInitializedList()
                }.SetCommonProperties();
            }

            using var receiveTimeout = new CancellationTokenSource(waitTime, _bus.TimeProvider);
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
            messages = ReceiveFifoMessages(queue, request.MaxNumberOfMessages.GetValueOrDefault(1), visibilityTimeout, request.MessageSystemAttributeNames, cancellationToken);
        }

        return new ReceiveMessageResponse
        {
            Messages = messages.ToInitializedList(),
        }.SetCommonProperties();

        void ReadAvailableMessages()
        {
            while (reader.TryRead(out var message))
            {
                ReceiveMessageImpl(message, ref messages, queue, visibilityTimeout, request.MessageSystemAttributeNames);
                if (messages is not null && messages.Count >= request.MaxNumberOfMessages.GetValueOrDefault(1))
                {
                    break;
                }
            }
        }
    }

    private void ReceiveMessageImpl(Message message, ref List<Message>? messages, SqsQueueResource queue, TimeSpan visibilityTimeout, List<string> requestedSystemAttributes)
    {
        if (IsAtMaxReceiveCount(message, queue) && queue.ErrorQueue is not null)
        {
            // Message has been received too many times, move it to the dead-letter queue.
            message.Attributes ??= [];
            message.Attributes[MessageSystemAttributeName.DeadLetterQueueSourceArn] = queue.Arn;
            EnqueueMessage(queue.ErrorQueue, message);
            return;
        }
        IncrementReceiveCount(message);

        var clonedMessage = CloneMessage(message);
        // Filter system attributes based on the request
        FilterSystemAttributes(clonedMessage, requestedSystemAttributes);
        var receiptHandle = CreateReceiptHandle(message, queue);
        clonedMessage.ReceiptHandle = receiptHandle;
        messages ??= [];
        messages.Add(clonedMessage);

        queue.InFlightMessages[receiptHandle] = (message,
            new SqsInflightMessageExpirationJob(receiptHandle, queue, visibilityTimeout, _bus.TimeProvider));
    }

    private List<Message>? ReceiveFifoMessages(SqsQueueResource queue, int maxMessages, TimeSpan visibilityTimeout, List<string> requestedSystemAttributes, CancellationToken cancellationToken)
    {
        List<Message>? messages = null;

        // Both regular FIFO and fair queues use the same receive logic
        // Fair queues differ in deduplication scope (per-group vs global), not in receive behavior
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

    public Task<SendMessageResponse> SendMessageAsync(string queueUrl, string messageBody,
        CancellationToken cancellationToken = default)
    {
        return SendMessageAsync(new SendMessageRequest
        {
            QueueUrl = queueUrl,
            MessageBody = messageBody
        }, cancellationToken);
    }

    public Task<SendMessageResponse> SendMessageAsync(SendMessageRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var queueName = GetQueueNameFromUrl(request.QueueUrl);
        if (!_bus.Queues.TryGetValue(queueName, out var queue))
        {
            throw new QueueDoesNotExistException("Queue not found");
        }

        var message = CreateMessage(request.MessageBody, request.MessageAttributes, request.MessageSystemAttributes);
        var totalSize = CalculateMessageSize(message.Body, message.MessageAttributes);

        if (totalSize > MaxMessageSize)
        {
            throw new AmazonSQSException(
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

            string deduplicationId = request.MessageDeduplicationId;
            if (string.IsNullOrEmpty(deduplicationId))
            {
                // Generate a deduplication ID based on the message body
                deduplicationId = GenerateMessageBodyHash(request.MessageBody);
            }

            message.Attributes[MessageSystemAttributeName.MessageDeduplicationId] = deduplicationId;

            // Check if this is a fair queue with per-message-group deduplication
            bool isFairQueue = IsFairQueue(queue);
            bool isDuplicate;

            if (isFairQueue)
            {
                // Per-message-group deduplication
                var groupDeduplicationIds = queue.MessageGroupDeduplicationIds.GetOrAdd(
                    request.MessageGroupId, 
                    _ => new ConcurrentDictionary<string, string>());
                isDuplicate = !groupDeduplicationIds.TryAdd(deduplicationId, message.MessageId);
                
                if (isDuplicate)
                {
                    // Message with this deduplication ID already exists in this group
                    return Task.FromResult(new SendMessageResponse
                    {
                        MessageId = groupDeduplicationIds[deduplicationId],
                        MD5OfMessageBody = message.MD5OfBody
                    }.SetCommonProperties());
                }
            }
            else
            {
                // Global deduplication (traditional FIFO)
                isDuplicate = !queue.DeduplicationIds.TryAdd(deduplicationId, message.MessageId);
                
                if (isDuplicate)
                {
                    // Message with this deduplication ID already exists, return existing message ID
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
            // TODO if DelaySeconds is set, we should use the default value for the queue
            if (request.DelaySeconds > 0)
            {
                message.Attributes ??= [];
                message.Attributes["DelaySeconds"] = request.DelaySeconds.Value.ToString(NumberFormatInfo.InvariantInfo);
                _ = SendDelayedMessageAsync(queue, message, request.DelaySeconds.Value);
            }
            else
            {
                queue.Messages.Writer.TryWrite(message);
            }
        }

        return Task.FromResult(new SendMessageResponse
        {
            MessageId = message.MessageId,
            MD5OfMessageBody = message.MD5OfBody
        }.SetCommonProperties());
    }

    private static int CalculateMessageSize(string messageBody, Dictionary<string, MessageAttributeValue>? messageAttributes)
    {
        var totalSize = 0;

        // Add message body size
        totalSize += Encoding.UTF8.GetByteCount(messageBody);

        // Add message attributes size
        if (messageAttributes != null)
        {
            foreach (var (key, attributeValue) in messageAttributes)
            {
                // Add attribute name size
                totalSize += Encoding.UTF8.GetByteCount(key);

                // Add data type size (including any custom type prefix)
                totalSize += Encoding.UTF8.GetByteCount(attributeValue.DataType);

                // Add value size based on the type
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
            if (!message.Attributes.TryGetValue("MessageGroupId", out var groupId) || string.IsNullOrEmpty(groupId))
            {
                // A message being moved to a FIFO DLQ must retain its Group ID.
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

    private static Message CreateMessage(string messageBody, Dictionary<string, MessageAttributeValue>? messageAttributes, Dictionary<string, MessageSystemAttributeValue> messageSystemAttributes)
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
        await Task.Delay(TimeSpan.FromSeconds(delaySeconds), _bus.TimeProvider).ConfigureAwait(true);
        queue.Messages.Writer.TryWrite(message);
    }

    public Task<CancelMessageMoveTaskResponse> CancelMessageMoveTaskAsync(CancelMessageMoveTaskRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!_bus.MoveTasks.TryGetValue(request.TaskHandle, out var task))
        {
            throw new ResourceNotFoundException("Task does not exist.");
        }

        task.MoveTaskJob.Dispose();
        task.Status = MoveTaskStatus.Cancelled;

        return Task.FromResult(new CancelMessageMoveTaskResponse
        {
            ApproximateNumberOfMessagesMoved = task.ApproximateNumberOfMessagesMoved
        }.SetCommonProperties());
    }

    public Task<ChangeMessageVisibilityResponse> ChangeMessageVisibilityAsync(string queueUrl, string receiptHandle,
        int? visibilityTimeout,
        CancellationToken cancellationToken = default)
    {
        return ChangeMessageVisibilityAsync(new ChangeMessageVisibilityRequest
        {
            QueueUrl = queueUrl,
            ReceiptHandle = receiptHandle,
            VisibilityTimeout = visibilityTimeout
        }, cancellationToken);
    }

    public Task<ChangeMessageVisibilityBatchResponse> ChangeMessageVisibilityBatchAsync(string queueUrl,
        List<ChangeMessageVisibilityBatchRequestEntry> entries,
        CancellationToken cancellationToken = default)
    {
        return ChangeMessageVisibilityBatchAsync(new ChangeMessageVisibilityBatchRequest
        {
            QueueUrl = queueUrl,
            Entries = entries
        }, cancellationToken);
    }

    public Task<ChangeMessageVisibilityBatchResponse> ChangeMessageVisibilityBatchAsync(
        ChangeMessageVisibilityBatchRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var queueName = GetQueueNameFromUrl(request.QueueUrl);
        if (!_bus.Queues.TryGetValue(queueName, out var queue))
        {
            throw new QueueDoesNotExistException($"Queue {request.QueueUrl} does not exist.");
        }

        var response = new ChangeMessageVisibilityBatchResponse
        {
            Successful = [],
            Failed = []
        };

        foreach (var entry in request.Entries)
        {
            try
            {
                if (queue.InFlightMessages.TryGetValue(entry.ReceiptHandle, out var message))
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

        return Task.FromResult(response.SetCommonProperties());
    }

    public Task<DeleteMessageBatchResponse> DeleteMessageBatchAsync(string queueUrl,
        List<DeleteMessageBatchRequestEntry> entries,
        CancellationToken cancellationToken = default)
    {
        return DeleteMessageBatchAsync(new DeleteMessageBatchRequest
        {
            QueueUrl = queueUrl,
            Entries = entries
        }, cancellationToken);
    }

    public Task<DeleteMessageBatchResponse> DeleteMessageBatchAsync(DeleteMessageBatchRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var queueName = GetQueueNameFromUrl(request.QueueUrl);
        if (!_bus.Queues.TryGetValue(queueName, out var queue))
        {
            throw new QueueDoesNotExistException($"Queue {request.QueueUrl} does not exist.");
        }

        var response = new DeleteMessageBatchResponse
        {
            Successful = [],
            Failed = []
        };

        foreach (var entry in request.Entries)
        {
            try
            {
                if (queue.InFlightMessages.Remove(entry.ReceiptHandle, out _))
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

        return Task.FromResult(response.SetCommonProperties());
    }

    public Task<DeleteQueueResponse> DeleteQueueAsync(DeleteQueueRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var queueName = GetQueueNameFromUrl(request.QueueUrl);
        _bus.Queues.TryRemove(queueName, out _);
        return Task.FromResult(new DeleteQueueResponse().SetCommonProperties());
    }

    public Task<GetQueueAttributesResponse> GetQueueAttributesAsync(string queueUrl, List<string> attributeNames,
        CancellationToken cancellationToken = default)
    {
        return GetQueueAttributesAsync(
            new GetQueueAttributesRequest
            {
                QueueUrl = queueUrl,
                AttributeNames = attributeNames
            },
            cancellationToken);
    }

    public Task<GetQueueAttributesResponse> GetQueueAttributesAsync(GetQueueAttributesRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var queueName = GetQueueNameFromUrl(request.QueueUrl);
        if (!_bus.Queues.TryGetValue(queueName, out var queue))
        {
            throw new QueueDoesNotExistException($"Queue {request.QueueUrl} does not exist.");
        }

        var attributes = new Dictionary<string, string>();

        if (request.AttributeNames.Count == 0 || request.AttributeNames.Contains("All"))
        {
            attributes = new Dictionary<string, string>(queue.Attributes ?? []);
            AddComputedAttributes(queue, attributes);
        }
        else
        {
            foreach (var attributeName in request.AttributeNames)
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
            attributes[attributeName] = "0"; // Assuming no delayed messages in this implementation
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

        if (!_bus.Queues.TryGetValue(request.QueueName, out var queue))
        {
            throw new QueueDoesNotExistException($"Queue {request.QueueName} does not exist.");
        }
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
            request.MaxResults.GetValueOrDefault(1000),
            request.NextToken);

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
            request.MaxResults.GetValueOrDefault(1000),
            request.NextToken);

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

        var queueName = GetQueueNameFromUrl(request.QueueUrl);
        if (!_bus.Queues.TryGetValue(queueName, out var queue))
        {
            throw new QueueDoesNotExistException("Queue not found.");
        }

        return Task.FromResult(new ListQueueTagsResponse
        {
            Tags = queue.Tags.ToInitializedDictionary()
        }.SetCommonProperties());
    }

    public Task<PurgeQueueResponse> PurgeQueueAsync(string queueUrl, CancellationToken cancellationToken = default)
    {
        return PurgeQueueAsync(new PurgeQueueRequest
        {
            QueueUrl = queueUrl
        }, cancellationToken);
    }

    public Task<PurgeQueueResponse> PurgeQueueAsync(PurgeQueueRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var queueName = GetQueueNameFromUrl(request.QueueUrl);
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
            queue.InFlightMessages.Remove(receipt, out var inFlightInfo);
            var (_, expirationHandler) = inFlightInfo;
            expirationHandler.Dispose();
        }

        return Task.FromResult(new PurgeQueueResponse().SetCommonProperties());
    }

    public Task<RemovePermissionResponse> RemovePermissionAsync(string queueUrl, string label,
        CancellationToken cancellationToken = default)
    {
        return RemovePermissionAsync(new RemovePermissionRequest
        {
            QueueUrl = queueUrl,
            Label = label
        }, cancellationToken);
    }

    public Task<RemovePermissionResponse> RemovePermissionAsync(RemovePermissionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var queueName = GetQueueNameFromUrl(request.QueueUrl);
        if (!_bus.Queues.TryGetValue(queueName, out var queue))
        {
            throw new QueueDoesNotExistException($"Queue {request.QueueUrl} does not exist.");
        }

        var policy = queue.Attributes?.TryGetValue("Policy", out var policyJson) == true
            ? Policy.FromJson(policyJson)
            : new Policy($"{queue.Arn}/SQSDefaultPolicy");

        var statementToRemove = policy.Statements.FirstOrDefault(s => s.Id == request.Label);
        if (statementToRemove == null)
        {
            throw new ArgumentException($"Value {request.Label} for parameter Label is invalid. Reason: can't find label.");
        }

        policy.Statements.Remove(statementToRemove);

        queue.Attributes ??= [];
        if (policy.Statements.Any())
        {
            queue.Attributes["Policy"] = policy.ToJson();
        }
        else
        {
            queue.Attributes.Remove("Policy");
        }

        return Task.FromResult(new RemovePermissionResponse().SetCommonProperties());
    }

    public Task<SendMessageBatchResponse> SendMessageBatchAsync(string queueUrl,
        List<SendMessageBatchRequestEntry> entries,
        CancellationToken cancellationToken = default)
    {
        return SendMessageBatchAsync(new SendMessageBatchRequest
        {
            QueueUrl = queueUrl,
            Entries = entries
        }, cancellationToken);
    }

    public Task<SendMessageBatchResponse> SendMessageBatchAsync(SendMessageBatchRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var queueName = GetQueueNameFromUrl(request.QueueUrl);
        if (!_bus.Queues.TryGetValue(queueName, out var queue))
        {
            throw new QueueDoesNotExistException($"Queue {request.QueueUrl} does not exist.");
        }

        var response = new SendMessageBatchResponse
        {
            Successful = [],
            Failed = []
        };

        var totalSize = request.Entries.Sum(e => CalculateMessageSize(e.MessageBody, e.MessageAttributes));

        if (totalSize > MaxMessageSize)
        {
            throw new BatchRequestTooLongException(
                $"Batch size ({totalSize} bytes) exceeds the maximum allowed size ({MaxMessageSize} bytes)");
        }

        foreach (var entry in request.Entries)
        {
            var message = CreateMessage(entry.MessageBody, entry.MessageAttributes, entry.MessageSystemAttributes);

            if (entry.DelaySeconds > 0)
            {
                message.Attributes["DelaySeconds"] = entry.DelaySeconds.Value.ToString(NumberFormatInfo.InvariantInfo);
                _ = SendDelayedMessageAsync(queue, message, entry.DelaySeconds.Value);
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

        return Task.FromResult(response.SetCommonProperties());
    }

    public Task<SetQueueAttributesResponse> SetQueueAttributesAsync(string queueUrl,
        Dictionary<string, string> attributes,
        CancellationToken cancellationToken = default)
    {
        return SetQueueAttributesAsync(new SetQueueAttributesRequest
        {
            QueueUrl = queueUrl,
            Attributes = attributes
        }, cancellationToken);
    }

    public Task<SetQueueAttributesResponse> SetQueueAttributesAsync(SetQueueAttributesRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var queueName = GetQueueNameFromUrl(request.QueueUrl);
        if (!_bus.Queues.TryGetValue(queueName, out var queue))
        {
            throw new QueueDoesNotExistException($"Queue {request.QueueUrl} does not exist.");
        }

        queue.Attributes ??= [];
        foreach (var (key, value) in request.Attributes)
        {
            queue.Attributes[key] = value;
        }

        UpdateQueueProperties(queue);

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

        var sourceQueueName = GetQueueNameFromArn(request.SourceArn);
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

        return Task.FromResult(new StartMessageMoveTaskResponse
        {
            TaskHandle = moveTask.TaskHandle
        }.SetCommonProperties());
    }

    public Task<TagQueueResponse> TagQueueAsync(TagQueueRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var queueName = GetQueueNameFromUrl(request.QueueUrl);
        if (!_bus.Queues.TryGetValue(queueName, out var queue))
        {
            throw new QueueDoesNotExistException("Queue not found.");
        }

        foreach (var tag in request.Tags ?? [])
        {
            queue.Tags ??= [];
            if (tag.Value is not null)
            {
                queue.Tags[tag.Key] = tag.Value;
            }
        }

        return Task.FromResult(new TagQueueResponse().SetCommonProperties());
    }

    public Task<UntagQueueResponse> UntagQueueAsync(UntagQueueRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var queueName = GetQueueNameFromUrl(request.QueueUrl);
        if (!_bus.Queues.TryGetValue(queueName, out var queue))
        {
            throw new QueueDoesNotExistException("Queue not found.");
        }

        foreach (var tagKey in request.TagKeys)
        {
            queue.Tags?.Remove(tagKey);
        }

        return Task.FromResult(new UntagQueueResponse().SetCommonProperties());
    }

    public Task<AddPermissionResponse> AddPermissionAsync(string queueUrl, string label, List<string> awsAccountIds,
        List<string> actions, CancellationToken cancellationToken)
    {
        return AddPermissionAsync(new AddPermissionRequest
        {
            QueueUrl = queueUrl,
            Label = label,
            AWSAccountIds = awsAccountIds,
            Actions = actions
        }, cancellationToken);
    }

    public Task<AddPermissionResponse> AddPermissionAsync(AddPermissionRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var queueName = GetQueueNameFromUrl(request.QueueUrl);
        if (!_bus.Queues.TryGetValue(queueName, out var queue))
        {
            throw new QueueDoesNotExistException("Queue not found.");
        }

        var policy = queue.Attributes?.TryGetValue("Policy", out var policyJson) == true
            ? Policy.FromJson(policyJson)
            : new Policy($"{queue.Arn}/SQSDefaultPolicy");

        var statement = new Statement(Statement.StatementEffect.Allow)
        {
            Id = request.Label,
            Actions = request.Actions.Select(action => new ActionIdentifier($"SQS:{action}")).ToList()
        };

        statement.Resources.Add(new Resource(queue.Arn));

        foreach (var accountId in request.AWSAccountIds)
        {
            statement.Principals.Add(new Principal($"arn:aws:iam::{accountId}:root"));
        }

        if (policy.CheckIfStatementExists(statement))
        {
            throw new ArgumentException($"Value {request.Label} for parameter Label is invalid. Reason: Already exists.");
        }

        policy.Statements.Add(statement);
        queue.Attributes ??= [];
        queue.Attributes["Policy"] = policy.ToJson();

        return Task.FromResult(new AddPermissionResponse().SetCommonProperties());
    }

    [UnsafeAccessor(UnsafeAccessorKind.Constructor)]
    private static extern SQSPaginatorFactory GetPaginatorFactory(IAmazonSQS client);

    public ISQSPaginatorFactory Paginators => _paginators.Value;
}
