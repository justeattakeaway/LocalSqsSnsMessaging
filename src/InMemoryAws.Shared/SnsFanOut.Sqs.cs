#pragma warning disable CS8600, CS8601, CS8602, CS8604 // Nullable reference warnings - internal POCOs use nullable properties but values are set at runtime

using InMemoryAws.Sns.Model;

namespace InMemoryAws;

/// <summary>Delivery to SQS queues, and to subscription dead-letter queues.</summary>
internal sealed partial class SnsFanOut
{
    private void DeliverToSqs(SnsSubscription subscription, OutboundMessage message)
    {
        // A queue that has been deleted since subscribing, or whose policy refuses SNS, is a
        // client-side error: SNS doesn't retry those, it dead-letters (or drops) the message straight away.
        if (CreateDelivery(subscription, subscription.EndPoint, message) is { } delivery
            && SqsServiceDelivery.Deliver(_cloud, delivery) is not (SqsServiceDelivery.Outcome.Delivered or SqsServiceDelivery.Outcome.Duplicate))
        {
            DeadLetter(subscription, message);
        }
    }

    /// <summary>
    /// Sends a message that couldn't be delivered to the subscription's dead-letter queue, if it has one.
    /// The DLQ receives exactly what the endpoint would have: <paramref name="deliveredBody"/> is the
    /// body that was actually attempted (so the envelope's timestamp isn't regenerated), or is built
    /// fresh when no attempt was made. A dead-letter queue that is missing, or whose policy refuses
    /// SNS, loses the message.
    /// </summary>
    private void DeadLetter(SnsSubscription subscription, OutboundMessage message, string? deliveredBody = null)
    {
        if (subscription.DeadLetterTargetArn is { } deadLetterArn
            && CreateDelivery(subscription, deadLetterArn, message, deliveredBody) is { } delivery)
        {
            SqsServiceDelivery.Deliver(_cloud, delivery);
        }
    }

    /// <summary>
    /// What SNS delivers to the queue <paramref name="queueArn"/> for <paramref name="subscription"/>:
    /// the JSON envelope, or for raw delivery the bare body with the message attributes as SQS
    /// message attributes, in the published message group and with the topic's deduplication id.
    /// Null when SQS would refuse the message (a raw delivery with more than ten attributes), so it
    /// is dropped.
    /// </summary>
    private SqsServiceDelivery.Request? CreateDelivery(SnsSubscription subscription, string queueArn, OutboundMessage message, string? body = null)
    {
        Dictionary<string, Sqs.Model.MessageAttributeValue>? attributes = null;
        if (subscription.Raw)
        {
            if (!SnsRawMessageAttributes.TryConvert(message.Attributes, out attributes))
            {
                return null;
            }

            body ??= message.Body;
        }
        else
        {
            // The attributes travel inside the envelope; the SQS message itself carries none.
            body ??= CreateSnsEnvelope(subscription, message);
        }

        return new SqsServiceDelivery.Request(queueArn, SqsServiceDelivery.SnsPrincipal, _topic.Arn, body)
        {
            MessageAttributes = attributes,
            MessageGroupId = message.MessageGroupId,
            DeduplicationId = message.DeduplicationId,
        };
    }
}
