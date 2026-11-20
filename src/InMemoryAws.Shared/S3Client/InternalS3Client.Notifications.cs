using System.Globalization;
using InMemoryAws.S3.Model;
using InMemoryAws.Sqs.Model;
using SnsPublishRequest = InMemoryAws.Sns.Model.PublishRequest;

namespace InMemoryAws.S3;

/// <summary>
/// Event notifications: a bucket's notification configuration, and the events S3 sends to SQS
/// queues, SNS topics and EventBridge when objects are written, deleted, tagged or have their ACL
/// changed.
/// </summary>
/// <remarks>
/// <para>
/// PutBucketNotificationConfiguration validates destinations when it is called, as S3 does: a queue
/// or topic must exist, in the bucket's region, and its policy must grant <c>s3.amazonaws.com</c>
/// <c>sqs:SendMessage</c> or <c>sns:Publish</c> for the bucket (<c>aws:SourceArn</c>, and
/// <c>aws:SourceAccount</c>, the bucket owner). Lambda functions are not emulated, so a Lambda
/// destination never validates. Each destination the configuration adds receives an
/// <c>s3:TestEvent</c>.
/// </para>
/// <para>
/// Events are delivered during the request that causes them, in the order the store commits them,
/// through the same paths SNS and EventBridge deliver by (<see cref="SqsServiceDelivery"/>,
/// <see cref="SnsTopicResource.Publish"/>, the default bus's rules). A destination that no longer
/// admits S3 drops the event, as on AWS.
/// </para>
/// </remarks>
internal sealed partial class InternalS3Client
{
    /// <summary>The request id the wire protocol answers with, which events name; null outside HTTP.</summary>
    internal string? RequestId { get; init; }

    public override Task<NotificationConfiguration> GetBucketNotificationConfigurationAsync(GetBucketNotificationConfigurationRequest request, CancellationToken cancellationToken)
    {
        lock (Store.Gate)
        {
            var bucket = GetBucket(request.Bucket);
            return Task.FromResult(bucket.Notifications ?? new NotificationConfiguration());
        }
    }

    /// <summary>
    /// Replaces the bucket's notification configuration, whole or not at all: the configuration is
    /// checked (<see cref="S3NotificationRules.Validate"/>), then, unless the request skips it, every
    /// destination is validated; any destination that fails refuses the request, naming each one.
    /// </summary>
    public override Task<PutBucketNotificationConfigurationOutput> PutBucketNotificationConfigurationAsync(PutBucketNotificationConfigurationRequest request, CancellationToken cancellationToken)
    {
        var configuration = request.NotificationConfiguration ?? throw S3Errors.MalformedXml();
        lock (Store.Gate)
        {
            var bucket = GetBucket(request.Bucket);
            S3NotificationRules.Validate(configuration, bucket.Region);
            var destinations = S3NotificationRules.Entries(configuration).Select(e => (e.Kind, Arn: e.Arn!)).Distinct().ToList();
            if (request.SkipDestinationValidation != true)
            {
                var failures = destinations
                    .Select(d => (d.Arn, Reason: DestinationProblem(bucket, d.Kind, d.Arn)))
                    .Where(f => f.Reason is not null)
                    .Select(f => (f.Arn, f.Reason!))
                    .ToList();
                if (failures.Count > 0)
                {
                    throw S3NotificationRules.Unvalidated(failures);
                }
            }

            var previous = bucket.Notifications is { } old
                ? S3NotificationRules.Entries(old).Select(e => e.Arn).ToHashSet(StringComparer.Ordinal)
                : [];
            var empty = configuration.TopicConfigurations is not { Count: > 0 }
                && configuration.QueueConfigurations is not { Count: > 0 }
                && configuration.LambdaFunctionConfigurations is not { Count: > 0 }
                && configuration.EventBridgeConfiguration is null;
            bucket.Notifications = empty ? null : configuration;

            if (request.SkipDestinationValidation != true)
            {
                var test = S3EventMessage.TestEvent(bucket.Name, Now, EventRequestId, NewHostId());
                foreach (var (kind, arn) in destinations.Where(d => !previous.Contains(d.Arn)))
                {
                    Send(bucket, kind, arn, test);
                }
            }

            return Task.FromResult(new PutBucketNotificationConfigurationOutput());
        }
    }

    /// <summary>
    /// Why S3 could not validate a destination, or null when it can deliver there: the queue or topic
    /// must exist and grant S3 the right to deliver for this bucket. Lambda is not emulated.
    /// </summary>
    private string? DestinationProblem(S3Bucket bucket, string kind, string arn)
    {
        switch (kind)
        {
            case "Queue":
                if (!_cloud.TryGetQueueByArn(arn, out var queue))
                {
                    return "The destination queue does not exist";
                }

                var queuePolicy = ResourcePolicy.Parse(ResourcePolicyKind.Queue, queue.Attributes?.GetValueOrDefault(InternalQueueAttributeName.Policy));
                return queuePolicy?.GrantsServiceRequest(S3Request(SqsActionName.SendMessage, queue.Arn, bucket)) == true
                    ? null
                    : "Permissions on the destination queue do not allow S3 to publish notifications from this bucket";
            case "Topic":
                if (!_cloud.TryGetTopicByArn(arn, out var topic))
                {
                    return "The destination topic does not exist";
                }

                return TopicPolicy(topic)?.GrantsServiceRequest(S3Request("Publish", topic.Arn, bucket)) == true
                    ? null
                    : "Permissions on the destination topic do not allow S3 to publish notifications from this bucket";
            default:
                return $"Not authorized to invoke function [{arn}]";
        }
    }

    private static ResourcePolicy? TopicPolicy(SnsTopicResource topic)
    {
        string? policy;
        lock (topic.SubscriptionGate)
        {
            policy = topic.Attributes.GetValueOrDefault(SnsTopicAttributes.Policy);
        }

        return ResourcePolicy.Parse(ResourcePolicyKind.Topic, policy);
    }

    /// <summary>
    /// S3 acting for <paramref name="bucket"/>: <c>aws:SourceArn</c> is the bucket's ARN, and
    /// <c>aws:SourceAccount</c> its owner's account (a bucket ARN names none). S3 sets no
    /// <c>aws:SourceOwner</c>.
    /// </summary>
    private ResourcePolicy.RequestContext S3Request(string action, string resource, S3Bucket bucket) =>
        ResourcePolicy.ServiceRequest(action, resource, SqsServiceDelivery.S3Principal, bucket.Arn) with
        {
            SourceAccount = _cloud.CurrentAccountId,
            SetsSourceOwner = false,
        };

    /// <summary>
    /// Sends a message to a queue or topic destination. A destination that is gone, or whose policy
    /// no longer admits S3, drops it.
    /// </summary>
    private void Send(S3Bucket bucket, string kind, string arn, string body)
    {
        switch (kind)
        {
            case "Queue":
                SqsServiceDelivery.Deliver(_cloud, new SqsServiceDelivery.Request(arn, SqsServiceDelivery.S3Principal, bucket.Arn, body)
                {
                    SourceAccount = _cloud.CurrentAccountId,
                });
                break;
            case "Topic":
                if (_cloud.TryGetTopicByArn(arn, out var topic)
                    && !topic.IsFifo
                    && (TopicPolicy(topic)?.EvaluateServiceDelivery(S3Request("Publish", topic.Arn, bucket)) ?? ResourcePolicy.ServiceDeliveryVerdict.Admitted)
                        == ResourcePolicy.ServiceDeliveryVerdict.Admitted)
                {
                    topic.Publish(new SnsPublishRequest { TopicArn = topic.Arn, Message = body, Subject = S3EventMessage.Subject });
                }

                break;
        }
    }

    partial void OnObjectWritten(S3Bucket bucket, S3ObjectVersion version, string eventName) =>
        NotifyObjectEvent(bucket, version, eventName);

    partial void OnObjectRemoved(S3Bucket bucket, S3ObjectVersion version, string eventName) =>
        NotifyObjectEvent(bucket, version, eventName);

    partial void OnObjectTagged(S3Bucket bucket, S3ObjectVersion version, string eventName) =>
        NotifyObjectEvent(bucket, version, eventName);

    partial void OnObjectAclPut(S3Bucket bucket, S3ObjectVersion version) =>
        NotifyObjectEvent(bucket, version, "ObjectAcl:Put");

    /// <summary>
    /// Sends an object event to every destination of the bucket's configuration that subscribes to
    /// it, and to EventBridge when the bucket sends events there. Call under the store's lock, after
    /// the change is made: <paramref name="eventName"/> is S3's name for it without <c>s3:</c>
    /// (<c>ObjectCreated:Put</c>, <c>ObjectRemoved:DeleteMarkerCreated</c>, <c>ObjectAcl:Put</c>,
    /// <c>ObjectRestore:Post</c>, ...).
    /// </summary>
    /// <param name="bucket">The bucket.</param>
    /// <param name="version">The version the event is about: the one written, removed or changed (a delete marker for a delete that made one).</param>
    /// <param name="eventName">The event.</param>
    /// <param name="reason">EventBridge's <c>reason</c>, where it isn't the event's usual one.</param>
    internal void NotifyObjectEvent(S3Bucket bucket, S3ObjectVersion version, string eventName, string? reason = null)
    {
        if (bucket.Notifications is not { } configuration)
        {
            return;
        }

        var created = eventName.StartsWith("ObjectCreated:", StringComparison.Ordinal);
        var removed = eventName.StartsWith("ObjectRemoved:", StringComparison.Ordinal);
        var describesData = !removed && !version.IsDeleteMarker;
        var e = new S3ObjectEvent(
            eventName,
            bucket.Name,
            bucket.Region,
            version.Key,
            Now,
            EventRequestId,
            NewHostId(),
            _cloud.CurrentAccountId,
            bucket.OwnerId,
            SourceIp)
        {
            Size = describesData && (created || eventName.StartsWith("ObjectRestore:", StringComparison.Ordinal)) ? version.Size : null,
            ETag = describesData ? version.ETag.Trim('"') : null,
            VersionId = bucket.Versioning is null ? null : version.VersionId,
            Sequencer = created || removed ? NextSequencer(bucket) : null,
            Reason = reason,
            StorageClass = version.StorageClass,
        };

        foreach (var topic in configuration.TopicConfigurations ?? [])
        {
            if (S3NotificationRules.Matches(topic.Events, topic.Filter, eventName, version.Key))
            {
                Send(bucket, "Topic", topic.TopicArn!, S3EventMessage.Records(e, topic.Id));
            }
        }

        foreach (var queue in configuration.QueueConfigurations ?? [])
        {
            if (S3NotificationRules.Matches(queue.Events, queue.Filter, eventName, version.Key))
            {
                Send(bucket, "Queue", queue.QueueArn!, S3EventMessage.Records(e, queue.Id));
            }
        }

        if (configuration.EventBridgeConfiguration is not null && S3EventMessage.EventBridge(e) is { } bridged)
        {
            new InternalEventBridgeClient(_cloud, bucket.Region).PutServiceEvent("aws.s3", bridged.DetailType, [bucket.Arn], bridged.Detail);
        }
    }

    /// <summary>
    /// The next sequencer of the bucket's object events: 18 upper-case hex digits that grow with
    /// every create and delete, so they order the events on a key as the store committed them.
    /// </summary>
    private string NextSequencer(S3Bucket bucket)
    {
        bucket.LastSequencer = Math.Max(bucket.LastSequencer + 1, _cloud.TimeProvider.GetUtcNow().UtcTicks);
        return bucket.LastSequencer.ToString("X18", CultureInfo.InvariantCulture);
    }

    /// <summary>The id events give the request: the one the wire protocol answers with, or a new one.</summary>
    private string EventRequestId => RequestId ?? RandomToken(12).ToUpperInvariant();

    /// <summary>Where the request came from, as events report it.</summary>
    private string SourceIp => Request?.Header("X-Forwarded-For")?.Split(',')[0].Trim() is { Length: > 0 } forwarded ? forwarded : "127.0.0.1";

    /// <summary>An extended request id (<c>x-amz-id-2</c>), for events.</summary>
    private static string NewHostId()
    {
        var buffer = new byte[48];
        Random.GetBytes(buffer);
        return Convert.ToBase64String(buffer);
    }
}
