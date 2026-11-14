#pragma warning disable CS8600, CS8601, CS8602, CS8604 // Nullable reference warnings - internal POCOs use nullable properties but values are set at runtime

using LocalSqsSnsMessaging.Sns.Model;

namespace LocalSqsSnsMessaging;

/// <summary>Publish and PublishBatch.</summary>
internal sealed partial class InternalSnsClient
{
    public Task<PublishResponse> PublishAsync(PublishRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        // The limit is per-topic, so the topic has to be resolved before the message can be sized up.
        var topic = GetTopicByArn(request.TopicArn);

        var maxMessageSize = GetMaxMessageSize(topic);
        var messageSize = CalculateMessageSize(request.Message, request.Subject, request.MessageAttributes);
        if (messageSize > maxMessageSize)
        {
            throw new InternalInvalidParameterException("Invalid parameter: Message too long");
        }

        var result = topic.PublishAction.Execute(request);

        _bus.RecordOperation(AwsServiceName.Sns, SnsActionName.Publish, request.TopicArn);
        return Task.FromResult(result);
    }

    public Task<PublishBatchResponse> PublishBatchAsync(PublishBatchRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var topic = GetTopicByArn(request.TopicArn);

        // The combined size of the batch is measured against the topic's limit, the same one a
        // single Publish is measured against.
        var maxMessageSize = GetMaxMessageSize(topic);
        var totalSize = (request.PublishBatchRequestEntries ?? [])
            .Sum(requestEntry => CalculateMessageSize(requestEntry.Message, requestEntry.Subject, requestEntry.MessageAttributes));
        if (totalSize > maxMessageSize)
        {
            throw new InternalBatchRequestTooLongException(
                "The length of all the messages put together is more than the limit.");
        }

        var result = topic.PublishAction.ExecuteBatch(request);

        _bus.RecordOperation(AwsServiceName.Sns, SnsActionName.PublishBatch, request.TopicArn);
        return Task.FromResult(result);
    }

    private static int CalculateMessageSize(string message, string? subject, Dictionary<string, MessageAttributeValue>? messageAttributes)
    {
        var totalSize = 0;

        // Add message body size
        totalSize += Encoding.UTF8.GetByteCount(message ?? string.Empty);

        // Add subject size
        if (!string.IsNullOrEmpty(subject))
        {
            totalSize += Encoding.UTF8.GetByteCount(subject);
        }

        // Add message attributes size
        if (messageAttributes != null)
        {
            foreach (var (key, attributeValue) in messageAttributes)
            {
                // Add attribute name size
                totalSize += Encoding.UTF8.GetByteCount(key);

                // Add data type size (including any custom type prefix)
                totalSize += Encoding.UTF8.GetByteCount(attributeValue.DataType ?? string.Empty);

                // Add value size based on the type
                if (attributeValue.BinaryValue != null)
                {
                    totalSize += (int)attributeValue.BinaryValue.Length;
                }
                else if (attributeValue.StringValue != null)
                {
                    totalSize += Encoding.UTF8.GetByteCount(attributeValue.StringValue);
                }
            }
        }

        return totalSize;
    }
}
