using System.Security.Cryptography;
using InMemoryAws.Sqs.Model;

namespace InMemoryAws;

/// <summary>
/// Service delivery: a message an AWS service (SNS for a subscription, EventBridge for a rule's
/// target) puts on a queue on behalf of one of its resources. This is the one place that decides
/// whether the queue takes it and what the queue receives, so SNS and EventBridge only say what
/// to deliver.
/// </summary>
/// <remarks>
/// <para>
/// The queue is found by its whole ARN when the delivery is made (a queue deleted since the
/// subscription or target was set is <see cref="Outcome.QueueNotFound"/>), and its <c>Policy</c>
/// is evaluated for the service as <see cref="ResourcePolicy.EvaluateServiceDelivery"/> describes.
/// An admitted message is accepted as a send (<see cref="SqsQueueResource.AcceptSent"/>): stamped
/// with its <c>SentTimestamp</c>, delayed by the queue's <c>DelaySeconds</c>, and on a FIFO queue
/// put in its group and deduplicated.
/// </para>
/// <para>
/// A service delivery carries no <c>SenderId</c>: AWS documents none for SNS or EventBridge
/// deliveries, so the library gives none rather than invent one. Nor does it check the queue's
/// KMS key or message size as <c>SendMessage</c> does: what SNS and EventBridge do with a queue
/// they cannot encrypt for is undocumented, and both services cap messages below SQS's limit.
/// </para>
/// </remarks>
internal static class SqsServiceDelivery
{
    /// <summary>The service principal SNS delivers as.</summary>
    public const string SnsPrincipal = "sns.amazonaws.com";

    /// <summary>The service principal EventBridge delivers as.</summary>
    public const string EventsPrincipal = "events.amazonaws.com";

    /// <summary>The service principal S3 delivers event notifications as.</summary>
    public const string S3Principal = "s3.amazonaws.com";

    /// <summary>What became of a service delivery.</summary>
    internal enum Outcome
    {
        /// <summary>The queue accepted the message.</summary>
        Delivered,

        /// <summary>A FIFO queue had already accepted a message with the same deduplication id; nothing was enqueued.</summary>
        Duplicate,

        /// <summary>No queue has the ARN.</summary>
        QueueNotFound,

        /// <summary>The queue's policy denies the service, for this source, explicitly.</summary>
        ExplicitlyDenied,

        /// <summary>The queue's policy says which sources the service may deliver for, and this is not one.</summary>
        NotAllowed,
    }

    /// <summary>One message a service delivers to a queue.</summary>
    /// <param name="QueueArn">The queue's ARN, as the subscription or target names it.</param>
    /// <param name="ServicePrincipal">The service delivering, e.g. <see cref="SnsPrincipal"/>.</param>
    /// <param name="SourceArn">The resource the service delivers for (<c>aws:SourceArn</c>): the topic, or the rule.</param>
    /// <param name="Body">The message body.</param>
    internal sealed record Request(string QueueArn, string ServicePrincipal, string SourceArn, string Body)
    {
        /// <summary>The message attributes the queue receives (an SNS raw delivery's, or EventBridge's dead-letter ones).</summary>
        public Dictionary<string, MessageAttributeValue>? MessageAttributes { get; init; }

        /// <summary>
        /// The message group: on a FIFO queue, the group the message joins (required there); on a
        /// standard queue, kept as an attribute for fair queueing.
        /// </summary>
        public string? MessageGroupId { get; init; }

        /// <summary>On a FIFO queue, the deduplication id; without one, the queue deduplicates by the body.</summary>
        public string? DeduplicationId { get; init; }

        /// <summary>The X-Ray trace header, already validated, which the queue keeps as <c>AWSTraceHeader</c>.</summary>
        public string? TraceHeader { get; init; }

        /// <summary>
        /// The account that owns the source, when its ARN names none (an S3 bucket's ARN): the
        /// delivery's <c>aws:SourceAccount</c>. Such a service sets no <c>aws:SourceOwner</c>.
        /// </summary>
        public string? SourceAccount { get; init; }
    }

    /// <summary>Delivers <paramref name="request"/>'s message to the queue it names, if that queue admits it.</summary>
    public static Outcome Deliver(InMemoryAwsCloud cloud, Request request)
    {
        if (!cloud.TryGetQueueByArn(request.QueueArn, out var queue))
        {
            return Outcome.QueueNotFound;
        }

        var policyRequest = ResourcePolicy.ServiceRequest(SqsActionName.SendMessage, queue.Arn, request.ServicePrincipal, request.SourceArn);
        if (request.SourceAccount is not null)
        {
            policyRequest = policyRequest with { SourceAccount = request.SourceAccount, SetsSourceOwner = false };
        }

        var policy = ResourcePolicy.Parse(ResourcePolicyKind.Queue, queue.Attributes?.GetValueOrDefault(InternalQueueAttributeName.Policy));
        switch (policy?.EvaluateServiceDelivery(policyRequest) ?? ResourcePolicy.ServiceDeliveryVerdict.Admitted)
        {
            case ResourcePolicy.ServiceDeliveryVerdict.ExplicitlyDenied:
                return Outcome.ExplicitlyDenied;
            case ResourcePolicy.ServiceDeliveryVerdict.NotAllowed:
                return Outcome.NotAllowed;
        }

        var message = new Message
        {
            MessageId = Guid.NewGuid().ToString(),
            Body = request.Body,
            MD5OfBody = Md5Hex(request.Body),
            MessageAttributes = request.MessageAttributes,
        };
        if (request.TraceHeader is not null)
        {
            message.Attributes = new() { [InternalMessageSystemAttributeName.AWSTraceHeader] = request.TraceHeader };
        }

        return queue.AcceptSent(message, request.MessageGroupId, request.DeduplicationId, delaySeconds: null, cloud.TimeProvider, out _)
            ? Outcome.Delivered
            : Outcome.Duplicate;
    }

    private static string Md5Hex(string body)
    {
#pragma warning disable CA5351, CA1308 // SQS's MD5 digest is lowercase hex
        return Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(body))).ToLowerInvariant();
#pragma warning restore CA5351, CA1308
    }
}
