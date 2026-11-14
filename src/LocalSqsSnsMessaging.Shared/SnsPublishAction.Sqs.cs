#pragma warning disable CS8600, CS8601, CS8602, CS8604 // Nullable reference warnings - internal POCOs use nullable properties but values are set at runtime

using System.Security.Cryptography;
using System.Text.Json.Nodes;
using LocalSqsSnsMessaging.Sns.Model;
using Message = LocalSqsSnsMessaging.Sqs.Model.Message;
using SqsMessageAttributeValue = LocalSqsSnsMessaging.Sqs.Model.MessageAttributeValue;

namespace LocalSqsSnsMessaging;

/// <summary>Delivery to SQS queues, and to subscription dead-letter queues.</summary>
internal sealed partial class SnsPublishAction
{
    private void DeliverToSqs(SnsSubscription subscription, OutboundMessage message)
    {
        // A queue that has been deleted since subscribing, or whose policy refuses SNS, is a
        // client-side error: SNS doesn't retry those, it dead-letters (or drops) the message straight away.
        if (!TryGetQueue(subscription.EndPoint, out var queue) || !AdmitsDelivery(queue))
        {
            DeadLetter(subscription, message);
            return;
        }

        if (CreateSqsMessage(subscription, message) is { } sqsMessage)
        {
            Enqueue(queue, sqsMessage, message);
        }
    }

    /// <summary>
    /// Sends a message that couldn't be delivered to the subscription's dead-letter queue, if it has one.
    /// The DLQ receives exactly what the endpoint would have: <paramref name="deliveredBody"/> is the
    /// body that was actually attempted (so the envelope's timestamp isn't regenerated), or is built
    /// fresh when no attempt was made. A dead-letter queue whose policy refuses SNS loses the message.
    /// </summary>
    private void DeadLetter(SnsSubscription subscription, OutboundMessage message, string? deliveredBody = null)
    {
        if (subscription.DeadLetterTargetArn is null
            || !TryGetQueue(subscription.DeadLetterTargetArn, out var deadLetterQueue)
            || !AdmitsDelivery(deadLetterQueue))
        {
            return;
        }

        if (CreateSqsMessage(subscription, message, deliveredBody) is { } sqsMessage)
        {
            Enqueue(deadLetterQueue, sqsMessage, message);
        }
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
            if (message.MessageGroupId is not null)
            {
                // A standard queue keeps a published MessageGroupId (for fair queueing) without ordering by it.
                sqsMessage.Attributes ??= [];
                sqsMessage.Attributes[InternalMessageSystemAttributeName.MessageGroupId] = message.MessageGroupId;
            }

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

        // The topic's deduplication id (explicit or content-based) travels with the message; without
        // one, the queue deduplicates by the body it receives.
        var deduplicationId = message.DeduplicationId;
        if (string.IsNullOrEmpty(deduplicationId))
        {
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

    /// <summary>
    /// The SQS message SNS delivers for <paramref name="subscription"/>: the JSON envelope, or for raw
    /// delivery the bare body with the message attributes as SQS message attributes. Null when SQS
    /// would refuse the message (a raw delivery with more than ten attributes), so it is dropped.
    /// </summary>
    private Message? CreateSqsMessage(SnsSubscription subscription, OutboundMessage message, string? body = null)
    {
        Message sqsMessage;
        if (subscription.Raw)
        {
            if (!SnsRawMessageAttributes.TryConvert(message.Attributes, out var attributes))
            {
                return null;
            }

            sqsMessage = new Message { Body = body ?? message.Body, MessageAttributes = attributes };
        }
        else
        {
            // The attributes travel inside the envelope; the SQS message itself carries none.
            sqsMessage = new Message { Body = body ?? CreateSnsEnvelope(subscription, message) };
        }

        sqsMessage.MessageId = Guid.NewGuid().ToString();

#pragma warning disable CA5351
        var hash = MD5.HashData(Encoding.UTF8.GetBytes(sqsMessage.Body));
#pragma warning restore CA5351
#pragma warning disable CA1308
        sqsMessage.MD5OfBody = Convert.ToHexString(hash).ToLowerInvariant();
#pragma warning restore CA1308

        return sqsMessage;
    }
}
