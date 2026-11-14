namespace LocalSqsSnsMessaging;

/// <summary>The JSON envelope SNS wraps a notification in.</summary>
internal sealed partial class SnsPublishAction
{
    /// <summary>
    /// The notification as <paramref name="subscription"/> receives it when not in raw mode, signed
    /// under the topic's current <c>SignatureVersion</c> and naming the subscription in its
    /// <c>UnsubscribeURL</c>. Message attributes are carried verbatim, as published.
    /// </summary>
    private string CreateSnsEnvelope(SnsSubscription subscription, OutboundMessage message)
    {
        string signatureVersion;
        lock (_topic.SubscriptionGate)
        {
            signatureVersion = _topic.Attributes.TryGetValue(SnsTopicAttributes.SignatureVersion, out var version)
                ? version
                : SnsEnvelope.DefaultSignatureVersion;
        }

        return SnsEnvelope.Notification(
            _bus,
            signatureVersion,
            message.MessageId,
            message.SequenceNumber,
            message.TopicArn,
            message.Subject,
            message.Body,
            SnsHttpDelivery.UnsubscribeUrl(_bus, subscription),
            message.Attributes);
    }
}
