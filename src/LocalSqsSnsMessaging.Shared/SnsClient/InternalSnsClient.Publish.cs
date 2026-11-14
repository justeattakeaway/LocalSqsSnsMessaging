#pragma warning disable CS8600, CS8601, CS8602, CS8604 // Nullable reference warnings - internal POCOs use nullable properties but values are set at runtime

using LocalSqsSnsMessaging.Sns.Model;

namespace LocalSqsSnsMessaging;

/// <summary>Publish and PublishBatch.</summary>
internal sealed partial class InternalSnsClient
{
    public Task<PublishResponse> PublishAsync(PublishRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var targetArn = SnsPublishValidation.ResolveTarget(request);
        var topic = GetTopicByArn(targetArn, string.IsNullOrEmpty(request.TopicArn) ? "TargetArn" : "TopicArn");

        SnsPublishValidation.ValidateMessage(topic, request.Message, request.Subject, request.MessageStructure,
            request.MessageAttributes, request.MessageGroupId, request.MessageDeduplicationId);

        // The limit is per-topic, so the topic has to be resolved before the message can be sized up.
        if (SnsPublishValidation.MessageSize(request.Message, request.MessageAttributes) > GetMaxMessageSize(topic))
        {
            throw new InternalInvalidParameterException("Invalid parameter: Message too long");
        }

        var result = topic.PublishAction.Execute(request);

        _bus.RecordOperation(AwsServiceName.Sns, SnsActionName.Publish, topic.Arn);
        return Task.FromResult(result);
    }

    public Task<PublishBatchResponse> PublishBatchAsync(PublishBatchRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var topic = GetTopicByArn(request.TopicArn);

        // Every entry is checked before any is published: one invalid entry fails the whole request.
        var entries = request.PublishBatchRequestEntries;
        SnsPublishValidation.ValidateBatch(entries);
        foreach (var entry in entries!)
        {
            SnsPublishValidation.ValidateMessage(topic, entry.Message, entry.Subject, entry.MessageStructure,
                entry.MessageAttributes, entry.MessageGroupId, entry.MessageDeduplicationId);
        }

        // The combined size of the batch is measured against the topic's limit, the same one a
        // single Publish is measured against.
        var totalSize = entries.Sum(entry => SnsPublishValidation.MessageSize(entry.Message, entry.MessageAttributes));
        if (totalSize > GetMaxMessageSize(topic))
        {
            throw new InternalBatchRequestTooLongException(
                "The length of all the messages put together is more than the limit.");
        }

        var result = topic.PublishAction.ExecuteBatch(request);

        _bus.RecordOperation(AwsServiceName.Sns, SnsActionName.PublishBatch, request.TopicArn);
        return Task.FromResult(result);
    }
}
