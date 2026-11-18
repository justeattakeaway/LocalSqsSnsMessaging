#pragma warning disable CS8600, CS8601, CS8602, CS8604 // Nullable reference warnings - internal POCOs use nullable properties but values are set at runtime

using InMemoryAws.Sns.Model;

namespace InMemoryAws;

/// <summary>Publish and PublishBatch.</summary>
internal sealed partial class InternalSnsClient
{
    public Task<PublishResponse> PublishAsync(PublishRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var targetArn = SnsPublishValidation.ResolveTarget(request);
        var topic = GetTopicByArn(targetArn, string.IsNullOrEmpty(request.TopicArn) ? "TargetArn" : "TopicArn");
        Authorize(topic, SnsActionName.Publish);

        SnsPublishValidation.ValidateMessage(topic, request.Message, request.Subject, request.MessageStructure,
            request.MessageAttributes, request.MessageGroupId, request.MessageDeduplicationId);

        // The limit is per-topic, so the topic has to be resolved before the message can be sized up.
        if (SnsPublishValidation.MessageSize(request.Message, request.MessageAttributes) > topic.MaximumMessageSize)
        {
            throw new InternalInvalidParameterException("Invalid parameter: Message too long");
        }

        var result = topic.Publish(request);

        _cloud.RecordOperation(AwsServiceName.Sns, SnsActionName.Publish, topic.Arn);
        return Task.FromResult(result);
    }

    public Task<PublishBatchResponse> PublishBatchAsync(PublishBatchRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var topic = GetTopicByArn(request.TopicArn);
        // IAM has no PublishBatch action: a batch needs sns:Publish.
        Authorize(topic, SnsActionName.Publish);

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
        if (totalSize > topic.MaximumMessageSize)
        {
            throw new InternalBatchRequestTooLongException(
                "The length of all the messages put together is more than the limit.");
        }

        var result = topic.PublishBatch(request);

        _cloud.RecordOperation(AwsServiceName.Sns, SnsActionName.PublishBatch, request.TopicArn);
        return Task.FromResult(result);
    }
}
