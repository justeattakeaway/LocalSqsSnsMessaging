#pragma warning disable CS8600, CS8601, CS8602, CS8604, CS8619 // Nullable reference warnings - internal POCOs use nullable properties but values are set at runtime

using LocalSqsSnsMessaging.Sqs.Model;

namespace LocalSqsSnsMessaging;

/// <summary>CreateQueue, DeleteQueue, GetQueueUrl, ListQueues, ListDeadLetterSourceQueues and PurgeQueue.</summary>
internal sealed partial class InternalSqsClient
{
    public Task<CreateQueueResponse> CreateQueueAsync(CreateQueueRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var fifo = SqsQueueValidation.RequestsFifo(request.Attributes);
        SqsQueueValidation.ValidateQueueName(request.QueueName, fifo);
        var requestedAttributes = request.Attributes ?? [];
        SqsQueueValidation.ValidateAttributes(requestedAttributes, fifo, creating: true, current: new Dictionary<string, string>());
        SqsQueueValidation.ValidateRedriveAllowPolicyRegion(requestedAttributes, _region);
        if (request.Tags is { Count: > 0 })
        {
            SqsQueueValidation.ValidateTags(request.Tags);
            if (request.Tags.Count > SqsQueueValidation.MaxTagsPerQueue)
            {
                throw SqsQueueValidation.TooManyTags(request.QueueName!);
            }
        }

        if (Queues.TryGetValue(request.QueueName!, out var existingQueue))
        {
            // An existing name is only reused when everything asked for matches the queue as it is now.
            EnsureMatchesExistingQueue(existingQueue, requestedAttributes, request.Tags);
            _bus.RecordOperation(AwsServiceName.Sqs, SqsActionName.CreateQueue, existingQueue.Arn);
            return Task.FromResult(new CreateQueueResponse { QueueUrl = existingQueue.Url }.SetCommonProperties());
        }

        var queueUrl = _bus.ServiceUrl is not null
            ? $"{_bus.ServiceUrl.ToString().TrimEnd('/')}/{_bus.CurrentAccountId}/{request.QueueName}"
            : $"https://sqs.{_region}.amazonaws.com/{_bus.CurrentAccountId}/{request.QueueName}";
        var visibilityTimeoutParsed = request.Attributes?.TryGetValue(InternalQueueAttributeName.VisibilityTimeout, out var visibilityTimeout) == true
            ? TimeSpan.FromSeconds(int.Parse(visibilityTimeout, NumberFormatInfo.InvariantInfo))
            : TimeSpan.FromSeconds(30);

        var now = _bus.TimeProvider.GetUtcNow();
        var queue = new SqsQueueResource
        {
            Name = request.QueueName!,
            Region = _region,
            AccountId = _bus.CurrentAccountId,
            Bus = _bus,
            Url = queueUrl,
            VisibilityTimeout = visibilityTimeoutParsed,
            CreatedTimestamp = now,
            LastModifiedTimestamp = now,
            Attributes = [],
            Tags = request.Tags is { Count: > 0 } tags ? new Dictionary<string, string>(tags) : null
        };

        if (requestedAttributes.TryGetValue(InternalQueueAttributeName.RedrivePolicy, out var redrivePolicy))
        {
            ValidateRedrivePolicy(queue, redrivePolicy);
        }

        foreach (var requestAttribute in requestedAttributes)
        {
            queue.Attributes[requestAttribute.Key] = requestAttribute.Value;
        }

        StorePolicy(queue.Attributes);
        AddDefaultAttributes(queue);
        queue.Attributes.Add("QueueArn", queue.Arn);
        UpdateQueueProperties(queue);
        var regionQueues = _resources.Queues;
        if (!regionQueues.TryAdd(request.QueueName!, queue))
        {
            // Created concurrently under the same name: answer as for an existing queue.
            var winner = regionQueues[request.QueueName!];
            EnsureMatchesExistingQueue(winner, requestedAttributes, request.Tags);
            return Task.FromResult(new CreateQueueResponse { QueueUrl = winner.Url }.SetCommonProperties());
        }

        _bus.RecordOperation(AwsServiceName.Sqs, SqsActionName.CreateQueue, queue.Arn);

        var response = new CreateQueueResponse
        {
            QueueUrl = queueUrl
        };

        return Task.FromResult(response.SetCommonProperties());
    }

    /// <summary>
    /// <c>CreateQueue</c> on an existing name returns that queue only when every requested attribute
    /// and tag matches its current value; otherwise SQS refuses it with <c>QueueAlreadyExists</c>.
    /// </summary>
    private static void EnsureMatchesExistingQueue(SqsQueueResource queue, Dictionary<string, string> requestedAttributes, Dictionary<string, string>? requestedTags)
    {
        foreach (var (name, value) in requestedAttributes)
        {
            var requested = name == InternalQueueAttributeName.Policy && !string.IsNullOrEmpty(value) ? ResourcePolicy.Normalize(ResourcePolicyKind.Queue, value) : value;
            if (!SqsQueueValidation.SameAttributeValue(name, requested, queue.Attributes?.GetValueOrDefault(name)))
            {
                throw SqsRequestValidation.Error("QueueNameExists",
                    $"A queue already exists with the same name and a different value for attribute {name}");
            }
        }

        if (requestedTags is { Count: > 0 })
        {
            bool sameTags;
            lock (queue.TagsLock)
            {
                sameTags = queue.Tags is { } tags && tags.Count == requestedTags.Count
                           && requestedTags.All(tag => tags.TryGetValue(tag.Key, out var value) && value == tag.Value);
            }

            if (!sameTags)
            {
                throw SqsRequestValidation.Error("QueueNameExists",
                    "A queue already exists with the same name and a different value for tags ");
            }
        }
    }

    /// <summary>
    /// Fills in the attributes real SQS reports for a queue that was created without them, so
    /// <c>GetQueueAttributes</c> returns its defaults rather than omitting them.
    /// </summary>
    private static void AddDefaultAttributes(SqsQueueResource queue)
    {
        var attributes = queue.Attributes!;
        attributes.TryAdd(InternalQueueAttributeName.DelaySeconds, "0");
        attributes.TryAdd(InternalQueueAttributeName.MaximumMessageSize, "1048576");
        attributes.TryAdd(InternalQueueAttributeName.MessageRetentionPeriod, "345600");
        attributes.TryAdd(InternalQueueAttributeName.ReceiveMessageWaitTimeSeconds, "0");
        attributes.TryAdd(InternalQueueAttributeName.VisibilityTimeout, "30");

        if (attributes.ContainsKey(InternalQueueAttributeName.KmsMasterKeyId))
        {
            attributes.TryAdd(InternalQueueAttributeName.KmsDataKeyReusePeriodSeconds, "300");
            attributes.TryAdd(InternalQueueAttributeName.SqsManagedSseEnabled, "false");
        }
        else
        {
            attributes.TryAdd(InternalQueueAttributeName.SqsManagedSseEnabled, "true");
        }

        if (queue.IsFifo)
        {
            attributes.TryAdd(InternalQueueAttributeName.FifoQueue, "true");
            attributes.TryAdd(InternalQueueAttributeName.ContentBasedDeduplication, "false");
            attributes.TryAdd(InternalQueueAttributeName.DeduplicationScope, "queue");
            attributes.TryAdd(InternalQueueAttributeName.FifoThroughputLimit, "perQueue");
        }
    }

    public Task<DeleteQueueResponse> DeleteQueueAsync(DeleteQueueRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var queueName = GetQueueNameFromUrl(request.QueueUrl!);
        if (Queues.TryGetValue(queueName, out var queueToDelete))
        {
            Authorize(queueToDelete, SqsActionName.DeleteQueue);
        }

        if (!Queues.TryRemove(queueName, out var removedQueue))
        {
            throw new InternalQueueDoesNotExistException();
        }

        _bus.RecordOperation(AwsServiceName.Sqs, SqsActionName.DeleteQueue, removedQueue.Arn);
        return Task.FromResult(new DeleteQueueResponse().SetCommonProperties());
    }

    public Task<GetQueueUrlResponse> GetQueueUrlAsync(GetQueueUrlRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!Queues.TryGetValue(request.QueueName!, out var queue))
        {
            throw new InternalQueueDoesNotExistException();
        }
        Authorize(queue, SqsActionName.GetQueueUrl);
        _bus.RecordOperation(AwsServiceName.Sqs, SqsActionName.GetQueueUrl, queue.Arn);
        return Task.FromResult(new GetQueueUrlResponse { QueueUrl = queue.Url }.SetCommonProperties());
    }

    public Task<ListQueuesResponse> ListQueuesAsync(ListQueuesRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.MaxResults is < 1 or > 1000)
        {
            throw SqsRequestValidation.InvalidParameterValue(
                "Value for parameter MaxResults is invalid. Reason: MaxResults must be an integer between 1 and 1000.");
        }

        if (request.NextToken is { Length: > 0 } requestToken && !IsQueueUrlToken(requestToken))
        {
            throw SqsRequestValidation.InvalidParameterValue("Invalid or expired next token.");
        }

        var allQueues = Queues
            .Values
            .Where(q => string.IsNullOrEmpty(request.QueueNamePrefix) || q.Name.StartsWith(request.QueueNamePrefix, StringComparison.Ordinal))
            .Select(q => q.Url)
            .OrderBy(url => url)
            .ToList();

        var pagedQueues = new PaginatedList<string>(allQueues);

        var (items, nextToken) = pagedQueues.GetPage(
            TokenGenerator,
            request.MaxResults ?? 1000,
            request.NextToken);

        _bus.RecordOperation(AwsServiceName.Sqs, SqsActionName.ListQueues);
        return Task.FromResult(new ListQueuesResponse
        {
            QueueUrls = items,
            NextToken = nextToken
        }.SetCommonProperties());

        static string TokenGenerator(string x)
        {
            return Convert.ToBase64String(Encoding.UTF8.GetBytes(x));
        }
    }

    /// <summary>Whether a <c>ListQueues</c> NextToken is one it issued: the base64 of a queue URL.</summary>
    private static bool IsQueueUrlToken(string token)
    {
        try
        {
            return Encoding.UTF8.GetString(Convert.FromBase64String(token))
                .StartsWith("http", StringComparison.Ordinal);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    public Task<ListDeadLetterSourceQueuesResponse> ListDeadLetterSourceQueuesAsync(
        ListDeadLetterSourceQueuesRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var deadLetterQueueName = GetQueueNameFromUrl(request.QueueUrl!);
        if (!Queues.TryGetValue(deadLetterQueueName, out var deadLetterQueue))
        {
            throw new InternalQueueDoesNotExistException();
        }
        Authorize(deadLetterQueue, SqsActionName.ListDeadLetterSourceQueues);

        var deadLetterSourceQueues = Queues.Values
            .Where(q => string.Equals(q.DeadLetterTargetArn, deadLetterQueue.Arn, StringComparison.Ordinal))
            .Select(q => q.Url)
            .OrderBy(url => url)
            .ToList();

        var pagedQueues = new PaginatedList<string>(deadLetterSourceQueues);

        var (items, nextToken) = pagedQueues.GetPage(
            TokenGenerator,
            request.MaxResults ?? 1000,
            request.NextToken);

        _bus.RecordOperation(AwsServiceName.Sqs, SqsActionName.ListDeadLetterSourceQueues);
        return Task.FromResult(new ListDeadLetterSourceQueuesResponse
        {
            QueueUrls = items,
            NextToken = nextToken
        }.SetCommonProperties());

        static string TokenGenerator(string x)
        {
            return Convert.ToBase64String(Encoding.UTF8.GetBytes(x));
        }
    }

    public Task<PurgeQueueResponse> PurgeQueueAsync(PurgeQueueRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var queueName = GetQueueNameFromUrl(request.QueueUrl!);
        if (!Queues.TryGetValue(queueName, out var queue))
        {
            throw new InternalQueueDoesNotExistException();
        }
        Authorize(queue, SqsActionName.PurgeQueue);

        queue.Purge();

        _bus.RecordOperation(AwsServiceName.Sqs, SqsActionName.PurgeQueue, queue.Arn);
        return Task.FromResult(new PurgeQueueResponse().SetCommonProperties());
    }
}
