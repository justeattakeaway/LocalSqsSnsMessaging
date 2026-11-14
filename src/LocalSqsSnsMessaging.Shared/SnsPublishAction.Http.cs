#pragma warning disable CS8600, CS8601, CS8602, CS8604 // Nullable reference warnings - internal POCOs use nullable properties but values are set at runtime

using System.Security.Cryptography;
using System.Text.Json.Nodes;
using LocalSqsSnsMessaging.Sns.Model;
using Message = LocalSqsSnsMessaging.Sqs.Model.Message;
using SqsMessageAttributeValue = LocalSqsSnsMessaging.Sqs.Model.MessageAttributeValue;

namespace LocalSqsSnsMessaging;

/// <summary>Delivery to HTTP/S endpoints, retried per the delivery policy.</summary>
internal sealed partial class SnsPublishAction
{
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
}
