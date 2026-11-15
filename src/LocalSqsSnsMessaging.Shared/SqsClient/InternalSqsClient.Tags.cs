#pragma warning disable CS8600, CS8601, CS8602, CS8604, CS8619 // Nullable reference warnings - internal POCOs use nullable properties but values are set at runtime

using LocalSqsSnsMessaging.Sqs.Model;

namespace LocalSqsSnsMessaging;

/// <summary>TagQueue, UntagQueue and ListQueueTags.</summary>
internal sealed partial class InternalSqsClient
{
    public Task<TagQueueResponse> TagQueueAsync(TagQueueRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var queueName = GetQueueNameFromUrl(request.QueueUrl!);
        if (!Queues.TryGetValue(queueName, out var queue))
        {
            throw new InternalQueueDoesNotExistException();
        }
        Authorize(queue, SqsActionName.TagQueue);

        SqsQueueValidation.ValidateTags(request.Tags);

        lock (queue.TagsLock)
        {
            var newKeys = request.Tags!.Keys.Count(key => queue.Tags?.ContainsKey(key) != true);
            if ((queue.Tags?.Count ?? 0) + newKeys > SqsQueueValidation.MaxTagsPerQueue)
            {
                throw SqsQueueValidation.TooManyTags(queue.Name);
            }

            foreach (var tag in request.Tags)
            {
                queue.Tags ??= [];
                queue.Tags[tag.Key] = tag.Value;
            }
        }

        _bus.RecordOperation(AwsServiceName.Sqs, SqsActionName.TagQueue, queue.Arn);
        return Task.FromResult(new TagQueueResponse().SetCommonProperties());
    }

    public Task<UntagQueueResponse> UntagQueueAsync(UntagQueueRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var queueName = GetQueueNameFromUrl(request.QueueUrl!);
        if (!Queues.TryGetValue(queueName, out var queue))
        {
            throw new InternalQueueDoesNotExistException();
        }
        Authorize(queue, SqsActionName.UntagQueue);

        if (request.TagKeys is not { Count: > 0 })
        {
            throw SqsRequestValidation.MissingParameter("TagKeys");
        }

        foreach (var tagKey in request.TagKeys)
        {
            SqsQueueValidation.ValidateTagKey(tagKey);
        }

        lock (queue.TagsLock)
        {
            foreach (var tagKey in request.TagKeys)
            {
                queue.Tags?.Remove(tagKey);
            }
        }

        _bus.RecordOperation(AwsServiceName.Sqs, SqsActionName.UntagQueue, queue.Arn);
        return Task.FromResult(new UntagQueueResponse().SetCommonProperties());
    }

    public Task<ListQueueTagsResponse> ListQueueTagsAsync(ListQueueTagsRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var queueName = GetQueueNameFromUrl(request.QueueUrl!);
        if (!Queues.TryGetValue(queueName, out var queue))
        {
            throw new InternalQueueDoesNotExistException();
        }
        Authorize(queue, SqsActionName.ListQueueTags);

        Dictionary<string, string>? tags;
        lock (queue.TagsLock)
        {
            tags = queue.Tags.ToInitializedDictionary();
        }

        _bus.RecordOperation(AwsServiceName.Sqs, SqsActionName.ListQueueTags, queue.Arn);
        return Task.FromResult(new ListQueueTagsResponse
        {
            Tags = tags
        }.SetCommonProperties());
    }
}
