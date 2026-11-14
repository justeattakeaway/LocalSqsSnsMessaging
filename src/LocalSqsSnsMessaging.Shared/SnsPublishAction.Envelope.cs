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
        return SnsEnvelope.Notification(
            _bus,
            _topic.SignatureVersion ?? SnsEnvelope.DefaultSignatureVersion,
            message.MessageId,
            message.SequenceNumber,
            message.TopicArn,
            message.Subject,
            message.Body,
            SnsHttpDelivery.UnsubscribeUrl(_bus, subscription),
            message.Attributes,
            message.Stamp);
    }
}
