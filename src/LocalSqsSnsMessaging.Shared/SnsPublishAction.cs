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
internal sealed partial class SnsPublishAction
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
}
