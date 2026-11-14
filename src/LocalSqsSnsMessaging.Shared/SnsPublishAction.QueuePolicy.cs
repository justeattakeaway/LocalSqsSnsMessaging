namespace LocalSqsSnsMessaging;

/// <summary>Whether a subscribed queue's access policy lets SNS deliver to it.</summary>
internal sealed partial class SnsPublishAction
{
    /// <summary>The service principal SNS delivers to SQS as.</summary>
    private const string SnsServicePrincipal = "sns.amazonaws.com";

    /// <summary>
    /// Whether <paramref name="queue"/>'s <c>Policy</c> lets SNS send it this topic's
    /// notifications. SNS presents itself as <see cref="SnsServicePrincipal"/>, with the topic as
    /// <c>aws:SourceArn</c> and the topic's account as <c>aws:SourceAccount</c>. A queue with no
    /// policy, or one that says nothing about SNS, always admits the delivery; see
    /// <see cref="SqsQueuePolicy.AdmitsServiceDelivery"/>.
    /// </summary>
    private bool AdmitsDelivery(SqsQueueResource queue)
    {
        if (queue.Attributes?.TryGetValue(InternalQueueAttributeName.Policy, out var policy) != true
            || string.IsNullOrEmpty(policy))
        {
            return true;
        }

        var request = new SqsQueuePolicy.RequestContext(SqsActionName.SendMessage, queue.Arn,
            PrincipalAccount: null, PrincipalArn: null, SourceIp: null, SecureTransport: true)
        {
            Service = SnsServicePrincipal,
            SourceArn = _topic.Arn,
            SourceAccount = _topic.Arn.Split(':')[4],
        };
        return SqsQueuePolicy.AdmitsServiceDelivery(policy!, request);
    }
}
