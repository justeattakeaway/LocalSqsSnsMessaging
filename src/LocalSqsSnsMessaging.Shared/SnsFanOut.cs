#pragma warning disable CS8600, CS8601, CS8602, CS8604 // Nullable reference warnings - internal POCOs use nullable properties but values are set at runtime

using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using LocalSqsSnsMessaging.Sns.Model;
using Message = LocalSqsSnsMessaging.Sqs.Model.Message;
using SqsMessageAttributeValue = LocalSqsSnsMessaging.Sqs.Model.MessageAttributeValue;

namespace LocalSqsSnsMessaging;

/// <summary>
/// A topic's fan-out: hands a published message to each of the topic's confirmed subscriptions.
/// Applies each subscription's filter policy, delivers to SQS queues synchronously and to HTTP/S
/// endpoints in the background (with retries per the delivery policy), and dead-letters messages
/// that can't be delivered when the subscription has a redrive policy. Part of
/// <see cref="SnsTopicResource"/>'s implementation: the topic builds one when it is created and
/// reaches it through its own publish and replay methods.
/// </summary>
internal sealed partial class SnsFanOut
{
    private readonly SnsTopicResource _topic;
    private readonly InMemoryAwsBus _bus;

    public SnsFanOut(SnsTopicResource topic, InMemoryAwsBus bus)
    {
        _topic = topic;
        _bus = bus;
    }

    /// <summary>
    /// A published message, independent of whether it came from Publish or PublishBatch. On a FIFO
    /// topic, <see cref="SequenceNumber"/> and <see cref="DeduplicationId"/> are the ones the topic
    /// assigned and deduplicated it by.
    /// </summary>
    internal sealed record OutboundMessage(
        string MessageId,
        string TopicArn,
        string? Subject,
        string Body,
        string? MessageStructure,
        Dictionary<string, MessageAttributeValue>? Attributes,
        string? MessageGroupId,
        string? DeduplicationId,
        string? SequenceNumber = null)
    {
        /// <summary>
        /// The message's envelope Timestamp and signatures, shared by every subscription (and every
        /// copy <c>with</c> makes of the message, such as a FIFO topic's sequenced one).
        /// </summary>
        public SnsNotificationStamp Stamp { get; init; } = new();
    }

    public PublishResponse Execute(PublishRequest request)
    {
        var (messageId, sequenceNumber) = Publish(new OutboundMessage(
            Guid.NewGuid().ToString(),
            _topic.Arn,
            request.Subject,
            request.Message,
            request.MessageStructure,
            request.MessageAttributes,
            request.MessageGroupId,
            request.MessageDeduplicationId));

        return new PublishResponse
        {
            MessageId = messageId,
            SequenceNumber = sequenceNumber
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
                var (messageId, sequenceNumber) = Publish(new OutboundMessage(
                    Guid.NewGuid().ToString(),
                    request.TopicArn,
                    entry.Subject,
                    entry.Message,
                    entry.MessageStructure,
                    entry.MessageAttributes,
                    entry.MessageGroupId,
                    entry.MessageDeduplicationId));

                response.Successful.Add(new PublishBatchResultEntry
                {
                    Id = entry.Id,
                    MessageId = messageId,
                    SequenceNumber = sequenceNumber
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

    /// <summary>
    /// Publishes one message, returning the <c>MessageId</c> and (on a FIFO topic) the
    /// <c>SequenceNumber</c> to report for it.
    /// </summary>
    private (string MessageId, string? SequenceNumber) Publish(OutboundMessage message)
    {
        if (_topic.Fifo is { } fifo)
        {
            return PublishToFifoTopic(fifo, message);
        }

        Deliver(message);
        return (message.MessageId, null);
    }

    /// <summary>
    /// Delivers to the subscriptions the topic fans out to at this moment. On a FIFO topic the caller
    /// holds the topic's FIFO gate, which subscription changes also take, so the set can't change
    /// mid-message.
    /// </summary>
    private void Deliver(OutboundMessage message)
    {
        foreach (var subscription in _topic.FanOutSubscriptions)
        {
            DeliverTo(subscription, message);
        }
    }

    private void DeliverTo(SnsSubscription subscription, OutboundMessage published)
    {
        // With MessageStructure=json each protocol has its own body.
        var message = published.MessageStructure is null
            ? published
            : published with { Body = SnsMessageStructure.Parse(published.Body, published.MessageStructure).BodyFor(subscription.Protocol) };
        if (!SnsFilterPolicy.Matches(subscription, message.Body, message.Attributes))
        {
            return;
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
