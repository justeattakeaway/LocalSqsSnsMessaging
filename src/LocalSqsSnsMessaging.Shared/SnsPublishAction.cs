#pragma warning disable CS8600, CS8601, CS8602, CS8604 // Nullable reference warnings - internal POCOs use nullable properties but values are set at runtime

using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using LocalSqsSnsMessaging.Sns.Model;
using Message = LocalSqsSnsMessaging.Sqs.Model.Message;
using SqsMessageAttributeValue = LocalSqsSnsMessaging.Sqs.Model.MessageAttributeValue;

namespace LocalSqsSnsMessaging;

/// <summary>
/// Fans a published message out to a topic's subscriptions: applies each subscription's filter
/// policy, delivers to SQS queues synchronously and to HTTP/S endpoints in the background (with
/// retries per the delivery policy), and dead-letters messages that can't be delivered when the
/// subscription has a redrive policy.
/// </summary>
internal sealed class SnsPublishAction
{
    internal static SnsPublishAction NullInstance { get; } = new([], null!, null!);

    private readonly List<SnsSubscription> _subscriptions;
    private readonly SnsTopicResource _topic;
    private readonly InMemoryAwsBus _bus;

    public SnsPublishAction(List<SnsSubscription> subscriptions, SnsTopicResource topic, InMemoryAwsBus bus)
    {
        _subscriptions = subscriptions;
        _topic = topic;
        _bus = bus;
    }

    /// <summary>A published message, independent of whether it came from Publish or PublishBatch.</summary>
    private sealed record OutboundMessage(
        string MessageId,
        string TopicArn,
        string? Subject,
        string Body,
        Dictionary<string, MessageAttributeValue>? Attributes,
        string? MessageGroupId,
        string? DeduplicationId);

    public PublishResponse Execute(PublishRequest request)
    {
        var messageId = Guid.NewGuid().ToString();

        Deliver(new OutboundMessage(
            messageId,
            request.TopicArn,
            request.Subject,
            request.Message,
            request.MessageAttributes,
            request.MessageGroupId,
            request.MessageDeduplicationId));

        return new PublishResponse
        {
            MessageId = messageId
        }.SetCommonProperties();
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
                Deliver(new OutboundMessage(
                    messageId,
                    request.TopicArn,
                    entry.Subject,
                    entry.Message,
                    entry.MessageAttributes,
                    entry.MessageGroupId,
                    entry.MessageDeduplicationId));

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
                response.Failed.Add(new BatchResultErrorEntry
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

    private void Deliver(OutboundMessage message)
    {
        foreach (var subscription in _subscriptions)
        {
            if (!SnsFilterPolicy.Matches(subscription, message.Body, message.Attributes))
            {
                continue;
            }

            if (subscription.IsSqs)
            {
                DeliverToSqs(subscription, message);
            }
            else if (subscription.IsHttp)
            {
                DeliverToHttp(subscription, message);
            }
        }
    }

    private void DeliverToSqs(SnsSubscription subscription, OutboundMessage message)
    {
        // A queue that has been deleted since subscribing is a client-side error: SNS doesn't
        // retry those, it dead-letters (or drops) the message straight away.
        if (!TryGetQueue(subscription.EndPoint, out var queue))
        {
            DeadLetter(subscription, message);
            return;
        }

        Enqueue(queue, CreateSqsMessage(subscription, message), message);
    }

    private void DeliverToHttp(SnsSubscription subscription, OutboundMessage message)
    {
        var policy = SnsDeliveryPolicy.Resolve(
            subscription.DeliveryPolicy,
            _topic.Attributes.TryGetValue("DeliveryPolicy", out var topicPolicy) ? topicPolicy : null);

        var body = subscription.Raw
            ? message.Body
            : CreateSnsEnvelope(subscription, message).ToJsonString();

        _ = DeliverToHttpAsync(subscription, message, body, policy);
    }

    private async Task DeliverToHttpAsync(SnsSubscription subscription, OutboundMessage message, string body, SnsDeliveryPolicy policy)
    {
        try
        {
            var delays = policy.GetRetryDelays();
            for (var attempt = 0; attempt <= delays.Count; attempt++)
            {
                if (attempt > 0 && delays[attempt - 1] > TimeSpan.Zero)
                {
                    await _bus.TimeProvider.Delay(delays[attempt - 1]).ConfigureAwait(false);
                }

                var outcome = await SnsHttpDelivery.PostAsync(
                    _bus, subscription, "Notification", message.MessageId, body, policy.ContentType, subscription.Raw)
                    .ConfigureAwait(false);

                if (outcome == SnsHttpDelivery.Outcome.Delivered)
                {
                    return;
                }
                if (outcome == SnsHttpDelivery.Outcome.Failed)
                {
                    break;
                }
            }

            DeadLetter(subscription, message);
        }
#pragma warning disable CA1031 // Background delivery must never surface an exception to the publisher.
        catch (Exception)
        {
            // Swallowed intentionally - see pragma above.
        }
#pragma warning restore CA1031
    }

    /// <summary>
    /// Sends a message that couldn't be delivered to the subscription's dead-letter queue, if it has one.
    /// The DLQ receives exactly what the endpoint would have (raw body or SNS envelope).
    /// </summary>
    private void DeadLetter(SnsSubscription subscription, OutboundMessage message)
    {
        if (subscription.DeadLetterTargetArn is null || !TryGetQueue(subscription.DeadLetterTargetArn, out var deadLetterQueue))
        {
            return;
        }

        Enqueue(deadLetterQueue, CreateSqsMessage(subscription, message), message);
    }

    private bool TryGetQueue(string arn, out SqsQueueResource queue)
    {
        queue = null!;
        return _bus.Queues.TryGetValue(arn.Split(':').Last(), out queue!);
    }

    private void Enqueue(SqsQueueResource queue, Message sqsMessage, OutboundMessage message)
    {
        if (!queue.IsFifo)
        {
            queue.Messages.Enqueue(sqsMessage);
            return;
        }

        sqsMessage.Attributes ??= [];
        sqsMessage.Attributes["MessageGroupId"] = message.MessageGroupId;
        sqsMessage.Attributes["SequenceNumber"] = FifoSequenceNumber.Next().ToString(NumberFormatInfo.InvariantInfo);
        sqsMessage.Attributes["SentTimestamp"] = _bus.TimeProvider.GetUtcNow().ToUnixTimeMilliseconds().ToString(NumberFormatInfo.InvariantInfo);

        var deduplicationId = message.DeduplicationId;
        if (string.IsNullOrEmpty(deduplicationId))
        {
            // Generate a deduplication ID based on the message body
            deduplicationId = GenerateMessageBodyHash(sqsMessage.Body);
        }

        sqsMessage.Attributes[InternalMessageSystemAttributeName.MessageDeduplicationId] = deduplicationId;

        if (IsFairQueue(queue))
        {
            // Per-message-group deduplication
            var groupDeduplicationIds = queue.MessageGroupDeduplicationIds.GetOrAdd(
                message.MessageGroupId,
                _ => new ConcurrentDictionary<string, string>());

            if (groupDeduplicationIds.TryAdd(deduplicationId, sqsMessage.MessageId))
            {
                queue.EnqueueFifoMessage(message.MessageGroupId, sqsMessage);
            }
        }
        else
        {
            // Global deduplication (traditional FIFO)
            if (queue.DeduplicationIds.TryAdd(deduplicationId, sqsMessage.MessageId))
            {
                queue.EnqueueFifoMessage(message.MessageGroupId, sqsMessage);
            }
        }
    }

    private static bool IsFairQueue(SqsQueueResource queue)
    {
        return queue.Attributes != null &&
               queue.Attributes.TryGetValue(InternalQueueAttributeName.DeduplicationScope, out var dedupScope) &&
               dedupScope == "messageGroup" &&
               queue.Attributes.TryGetValue(InternalQueueAttributeName.FifoThroughputLimit, out var throughputLimit) &&
               throughputLimit == "perMessageGroupId";
    }

    private Message CreateSqsMessage(SnsSubscription subscription, OutboundMessage message)
    {
        var sqsMessage = subscription.Raw
            ? CreateRawSqsMessage(message.Body, message.Attributes)
            : CreateFormattedMessage(CreateSnsEnvelope(subscription, message), message.TopicArn);

        sqsMessage.MessageId = Guid.NewGuid().ToString();

#pragma warning disable CA5351
        var hash = MD5.HashData(Encoding.UTF8.GetBytes(sqsMessage.Body));
#pragma warning restore CA5351
#pragma warning disable CA1308
        sqsMessage.MD5OfBody = Convert.ToHexString(hash).ToLowerInvariant();
#pragma warning restore CA1308

        return sqsMessage;
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

    private JsonObject CreateSnsEnvelope(SnsSubscription subscription, OutboundMessage message)
    {
        var snsMessage = new JsonObject
        {
            ["Type"] = "Notification",
            ["MessageId"] = message.MessageId,
            ["TopicArn"] = message.TopicArn,
            ["Message"] = message.Body,
            ["Timestamp"] = SnsHttpDelivery.Timestamp(_bus),
            ["SignatureVersion"] = "1",
            ["Signature"] = "EXAMPLE",
            ["SigningCertURL"] = "EXAMPLE",
            ["UnsubscribeURL"] = SnsHttpDelivery.UnsubscribeUrl(_bus, subscription)
        };

        if (message.Subject is not null)
        {
            snsMessage["Subject"] = message.Subject;
        }

        if (message.Attributes is not null && message.Attributes.Count > 0)
        {
            var messageAttributes = new JsonObject();
            foreach (var (key, value) in message.Attributes)
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
}
