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

            DeadLetter(subscription, message, body);
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
    /// The DLQ receives exactly what the endpoint would have: <paramref name="deliveredBody"/> is the
    /// body that was actually attempted (so the envelope's timestamp isn't regenerated), or is built
    /// fresh when no attempt was made.
    /// </summary>
    private void DeadLetter(SnsSubscription subscription, OutboundMessage message, string? deliveredBody = null)
    {
        if (subscription.DeadLetterTargetArn is null || !TryGetQueue(subscription.DeadLetterTargetArn, out var deadLetterQueue))
        {
            return;
        }

        Enqueue(deadLetterQueue, CreateSqsMessage(subscription, message, deliveredBody), message);
    }

    private bool TryGetQueue(string arn, out SqsQueueResource queue)
    {
        var found = _bus.TryGetQueueByArn(arn, out var match);
        queue = match!;
        return found;
    }

    private void Enqueue(SqsQueueResource queue, Message sqsMessage, OutboundMessage message)
    {
        if (!queue.IsFifo)
        {
            // SQS applies the subscribed queue's DelaySeconds to messages SNS delivers, too.
            if (queue.DelaySeconds > 0)
            {
                queue.EnqueueAfterDelay(sqsMessage, TimeSpan.FromSeconds(queue.DelaySeconds), _bus.TimeProvider);
            }
            else
            {
                queue.Messages.Enqueue(sqsMessage);
            }

            return;
        }

        sqsMessage.Attributes ??= [];
        sqsMessage.Attributes["MessageGroupId"] = message.MessageGroupId;
        sqsMessage.Attributes["SentTimestamp"] = _bus.TimeProvider.GetUtcNow().ToUnixTimeMilliseconds().ToString(NumberFormatInfo.InvariantInfo);

        var deduplicationId = message.DeduplicationId;
        if (string.IsNullOrEmpty(deduplicationId))
        {
            // Generate a deduplication ID based on the message body
            deduplicationId = SqsDeduplicationCache.ContentDeduplicationId(sqsMessage.Body);
        }

        sqsMessage.Attributes[InternalMessageSystemAttributeName.MessageDeduplicationId] = deduplicationId;

        // Applies the queue's DelaySeconds too, as for a message sent with SendMessage.
        queue.TryEnqueueFifoSend(sqsMessage, message.MessageGroupId, deduplicationId, IsFairQueue(queue), _bus.TimeProvider, out _);
    }

    private static bool IsFairQueue(SqsQueueResource queue)
    {
        return queue.Attributes != null &&
               queue.Attributes.TryGetValue(InternalQueueAttributeName.DeduplicationScope, out var dedupScope) &&
               dedupScope == "messageGroup" &&
               queue.Attributes.TryGetValue(InternalQueueAttributeName.FifoThroughputLimit, out var throughputLimit) &&
               throughputLimit == "perMessageGroupId";
    }

    private Message CreateSqsMessage(SnsSubscription subscription, OutboundMessage message, string? body = null)
    {
        var sqsMessage = subscription.Raw
            ? CreateRawSqsMessage(body ?? message.Body, message.Attributes)
            : CreateFormattedMessage(body ?? CreateSnsEnvelope(subscription, message).ToJsonString(), message.TopicArn);

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

    private static Message CreateFormattedMessage(string envelope, string topicArn)
    {
        return new Message
        {
            Body = envelope,
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

}
