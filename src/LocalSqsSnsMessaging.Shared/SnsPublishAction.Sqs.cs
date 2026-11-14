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

    /// <summary>
    /// Enqueues a delivery as a send: after the queue's DelaySeconds, and on a FIFO queue in the
    /// published message group, deduplicated by the topic's deduplication id or, without one, by
    /// the body the queue receives.
    /// </summary>
    private void Enqueue(SqsQueueResource queue, Message sqsMessage, OutboundMessage message) =>
        queue.AcceptSent(sqsMessage, message.MessageGroupId, message.DeduplicationId, delaySeconds: null, _bus.TimeProvider, out _);

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
