using System.Collections.Concurrent;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Amazon.SimpleNotificationService.Model;
using Amazon.SQS;
using Amazon.SQS.Model;
using MessageAttributeValue = Amazon.SimpleNotificationService.Model.MessageAttributeValue;
using SnsBatchResultErrorEntry = Amazon.SimpleNotificationService.Model.BatchResultErrorEntry;
using SqsMessageAttributeValue = Amazon.SQS.Model.MessageAttributeValue;

namespace LocalSqsSnsMessaging;

internal sealed class SnsPublishAction
{
    internal static SnsPublishAction NullInstance { get; } = new([], null!);

    private readonly List<(SnsSubscription Subscription, SqsQueueResource Queue)> _subscriptionsAndQueues;
    private readonly TimeProvider _timeProvider;
    private static Int128 _sequenceNumber = CreateSequenceNumber();
    private static SpinLock _sequenceSpinLock = new(false);

    private static Int128 CreateSequenceNumber()
    {
        var bytes = RandomNumberGenerator.GetBytes(16);
        var randomBigInt = new BigInteger(bytes);
        var twentyDigitBigInt = BigInteger.Abs(randomBigInt % BigInteger.Pow(10, 20));
        return (Int128)twentyDigitBigInt;
    }

    public SnsPublishAction(List<(SnsSubscription Subscription, SqsQueueResource Queue)> subscriptionsAndQueues, TimeProvider timeProvider)
    {
        _subscriptionsAndQueues = subscriptionsAndQueues;
        _timeProvider = timeProvider;
    }

    public PublishResponse Execute(PublishRequest request)
    {
        var messageId = Guid.NewGuid().ToString();

        foreach (var (subscription, queue) in _subscriptionsAndQueues)
        {
            var sqsMessage = CreateSqsMessage(request, messageId, subscription);
            if (queue.IsFifo)
            {
                sqsMessage.Attributes ??= [];
                sqsMessage.Attributes["MessageGroupId"] = request.MessageGroupId;
                sqsMessage.Attributes["SequenceNumber"] = GetNextSequenceNumber().ToString(NumberFormatInfo.InvariantInfo);
                sqsMessage.Attributes["SentTimestamp"] = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds().ToString(NumberFormatInfo.InvariantInfo);

                string deduplicationId = request.MessageDeduplicationId;
                if (string.IsNullOrEmpty(deduplicationId))
                {
                    // Generate a deduplication ID based on the message body
                    deduplicationId = GenerateMessageBodyHash(sqsMessage.Body);
                }

                sqsMessage.Attributes[MessageSystemAttributeName.MessageDeduplicationId] = deduplicationId;

                if (queue.DeduplicationIds.TryAdd(deduplicationId, sqsMessage.MessageId))
                {
                    EnqueueFifoMessage(queue, request.MessageGroupId, sqsMessage);
                }
            }
            else
            {
                queue.Messages.Writer.TryWrite(sqsMessage);
            }
        }

        return new PublishResponse
        {
            MessageId = messageId
        }.SetCommonProperties();
    }

    private static void EnqueueFifoMessage(SqsQueueResource queue, string messageGroupId, Message sqsMessage)
    {
        queue.MessageGroups.AddOrUpdate(messageGroupId,
            _ => new ConcurrentQueue<Message>([sqsMessage]),
            (_, existingQueue) =>
            {
                existingQueue.Enqueue(sqsMessage);
                return existingQueue;
            });
    }

    public PublishBatchResponse ExecuteBatch(PublishBatchRequest request)
    {
        var response = new PublishBatchResponse
        {
            Successful = [],
            Failed = []
        };

        foreach (var entry in request.PublishBatchRequestEntries)
        {
            try
            {
                var messageId = Guid.NewGuid().ToString();
                PublishSingleMessage(entry, request.TopicArn, messageId);
                response.Successful.Add(new PublishBatchResultEntry
                {
                    Id = entry.Id,
                    MessageId = messageId
                });
            }
#pragma warning disable CA1031
            catch (Exception ex)
#pragma warning restore CA1031
            {
                response.Failed.Add(new SnsBatchResultErrorEntry
                {
                    Id = entry.Id,
                    Code = "InternalError",
                    Message = ex.Message,
                    SenderFault = false
                });
            }
        }

        return response.SetCommonProperties();
    }

    private void PublishSingleMessage(PublishBatchRequestEntry entry, string topicArn, string messageId)
    {
        foreach (var (subscription, queue) in _subscriptionsAndQueues)
        {
            var sqsMessage = CreateSqsMessage(entry, topicArn, messageId, subscription);

            if (!queue.Messages.Writer.TryWrite(sqsMessage))
            {
                throw new InvalidOperationException("Failed to write message to queue.");
            }
        }
    }

    private Message CreateSqsMessage(PublishRequest request, string messageId, SnsSubscription subscription)
    {
        var message = subscription.Raw
            ? CreateRawSqsMessage(request.Message, request.MessageAttributes)
            : CreateFormattedSqsMessage(request, messageId);

#pragma warning disable CA5351
        var hash = MD5.HashData(Encoding.UTF8.GetBytes(message.Body));
#pragma warning restore CA5351
#pragma warning disable CA1308
        message.MD5OfBody = Convert.ToHexString(hash).ToLowerInvariant();
#pragma warning restore CA1308

        return message;
    }

    private Message CreateSqsMessage(PublishBatchRequestEntry entry, string topicArn, string messageId, SnsSubscription subscription)
    {
        var message = subscription.Raw
            ? CreateRawSqsMessage(entry.Message, entry.MessageAttributes)
            : CreateFormattedSqsMessage(entry, topicArn, messageId);

#pragma warning disable CA5351
        var hash = MD5.HashData(Encoding.UTF8.GetBytes(message.Body));
#pragma warning restore CA5351
#pragma warning disable CA1308
        message.MD5OfBody = Convert.ToHexString(hash).ToLowerInvariant();
#pragma warning restore CA1308

        return message;
    }

    private static Message CreateRawSqsMessage(string message, Dictionary<string, MessageAttributeValue>? attributes)
    {
        return new Message
        {
            Body = message,
            MessageAttributes = attributes?.ToDictionary(
                kvp => kvp.Key,
                kvp => new SqsMessageAttributeValue
                {
                    DataType = kvp.Value.DataType,
                    StringValue = kvp.Value.StringValue,
                    BinaryValue = kvp.Value.BinaryValue
                })
        };
    }

    private Message CreateFormattedSqsMessage(PublishRequest request, string messageId)
    {
        var snsMessage = CreateSnsMessage(messageId, request.TopicArn, request.Subject, request.Message, request.MessageAttributes);
        return CreateFormattedMessage(snsMessage, request.TopicArn);
    }

    private Message CreateFormattedSqsMessage(PublishBatchRequestEntry entry, string topicArn, string messageId)
    {
        var snsMessage = CreateSnsMessage(messageId, topicArn, entry.Subject, entry.Message, entry.MessageAttributes);
        return CreateFormattedMessage(snsMessage, topicArn);
    }

    private JsonObject CreateSnsMessage(string messageId, string topicArn, string? subject, string message, Dictionary<string, MessageAttributeValue>? attributes)
    {
        var snsMessage = new JsonObject
        {
            ["Type"] = "Notification",
            ["MessageId"] = messageId,
            ["TopicArn"] = topicArn,
            ["Message"] = message,
            ["Timestamp"] = _timeProvider.GetUtcNow().ToString("yyyy-MM-ddTHH:mm:ss.fffZ", DateTimeFormatInfo.InvariantInfo),
            ["SignatureVersion"] = "1",
            ["Signature"] = "EXAMPLE",
            ["SigningCertURL"] = "EXAMPLE",
            ["UnsubscribeURL"] = "EXAMPLE"
        };

        if (subject is not null)
        {
            snsMessage["Subject"] = subject;
        }

        if (attributes is not null && attributes.Count > 0)
        {
            var messageAttributes = new JsonObject();
            foreach (var (key, value) in attributes)
            {
                messageAttributes[key] = new JsonObject
                {
                    ["Type"] = value.DataType,
                    ["Value"] = value.StringValue ?? Convert.ToBase64String(value.BinaryValue.ToArray())
                };
            }
            snsMessage["MessageAttributes"] = messageAttributes;
        }

        return snsMessage;
    }

    private static Message CreateFormattedMessage(JsonNode snsMessage, string topicArn)
    {
        return new Message
        {
            Body = snsMessage.ToJsonString(),
            MessageAttributes = new Dictionary<string, SqsMessageAttributeValue>
            {
                ["TopicArn"] = new()
                {
                    DataType = "String",
                    StringValue = topicArn
                }
            }
        };
    }

    private static string GenerateMessageBodyHash(string messageBody)
    {
        var hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(messageBody));
        return Convert.ToBase64String(hashBytes);
    }

    private static Int128 GetNextSequenceNumber()
    {
        var lockTaken = false;
        try
        {
            _sequenceSpinLock.Enter(ref lockTaken);
            return ++_sequenceNumber;
        }
        finally
        {
            if (lockTaken) _sequenceSpinLock.Exit();
        }
    }
}
