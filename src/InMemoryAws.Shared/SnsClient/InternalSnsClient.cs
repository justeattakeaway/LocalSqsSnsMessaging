#pragma warning disable CS8600, CS8601, CS8602, CS8604 // Nullable reference warnings - internal POCOs use nullable properties but values are set at runtime

using InMemoryAws.Sns.Model;

namespace InMemoryAws;

/// <summary>
/// The in-memory SNS service. Topic operations are here; subscriptions, publishing, permissions and
/// tags, and the operations this cloud doesn't support are in the other parts of the class.
/// </summary>
internal sealed partial class InternalSnsClient
{
    private readonly InMemoryAwsCloud _cloud;

    /// <summary>The smallest value <c>MaximumMessageSize</c> can be set to: 1 KiB.</summary>
    private const int MinimumMaxMessageSize = 1024;

    /// <summary>The largest value <c>MaximumMessageSize</c> can be set to: 1 MiB.</summary>
    private const int LargestMaxMessageSize = 1048576;

    /// <summary>SNS answers every request for a topic that doesn't exist with this message.</summary>
    internal const string TopicDoesNotExist = "Topic does not exist";

    /// <summary>The resources of the region this client serves: it sees and creates only this region's topics.</summary>
    private readonly AwsRegionResources _resources;

    /// <param name="cloud">The cloud holding the topics.</param>
    /// <param name="region">
    /// The region the request was made in; <see langword="null"/> for the cloud's
    /// <see cref="InMemoryAwsCloud.CurrentRegion"/>.
    /// </param>
    internal InternalSnsClient(InMemoryAwsCloud cloud, string? region = null)
    {
        _cloud = cloud;
        _resources = cloud.Region(region);
    }

    public Task<CreateTopicResponse> CreateTopicAsync(CreateTopicRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var attributes = request.Attributes ?? [];
        var fifo = attributes.TryGetValue(SnsTopicAttributes.FifoTopic, out var fifoValue)
                   && string.Equals(fifoValue, "true", StringComparison.OrdinalIgnoreCase);
        if (!SnsTopicAttributes.IsValidTopicName(request.Name, fifo))
        {
            throw new InternalInvalidParameterException("Invalid parameter: Topic Name");
        }

        foreach (var (name, value) in attributes)
        {
            if (!SnsTopicAttributes.IsCreatable(name))
            {
                throw new InternalInvalidParameterException($"Invalid parameter: Attributes Reason: Unknown attribute {name}");
            }

            if (name.Equals(SnsTopicAttributes.MaximumMessageSize, StringComparison.Ordinal))
            {
                // A brand new topic has no subscriptions, so only the value itself needs checking.
                ValidateMaximumMessageSize(value, "Invalid parameter: Attributes Reason: ");
            }
            else if (!name.Equals(SnsTopicAttributes.FifoTopic, StringComparison.Ordinal))
            {
                SnsTopicAttributes.Validate(name, value, current: null);
            }
        }

        SnsTagValidation.ValidateTags(request.Tags, "tags");

        var topicArn = $"arn:aws:sns:{_resources.Region}:{_cloud.CurrentAccountId}:{request.Name}";
        var topic = new SnsTopicResource(_cloud, _resources)
        {
            Name = request.Name,
            Arn = topicArn,
            Fifo = fifo ? new SnsFifoTopicState() : null,
        };
        topic.Attributes[SnsTopicAttributes.DisplayName] = string.Empty;
        topic.Attributes[SnsTopicAttributes.Policy] = SnsTopicAttributes.DefaultPolicy(topicArn, _cloud.CurrentAccountId);
        if (fifo)
        {
            topic.Attributes[SnsTopicAttributes.FifoTopic] = "true";
            topic.Attributes[SnsTopicAttributes.ContentBasedDeduplication] = "false";
        }

        var now = _cloud.TimeProvider.GetUtcNow();
        foreach (var (name, value) in attributes)
        {
            if (!name.Equals(SnsTopicAttributes.FifoTopic, StringComparison.Ordinal))
            {
                topic.SetAttribute(name, value, now);
            }
        }

        foreach (var tag in request.Tags ?? [])
        {
            topic.Tags[tag.Key] = tag.Value;
        }

        var existing = _resources.Topics.GetOrAdd(request.Name, topic);
        if (!ReferenceEquals(existing, topic))
        {
            // CreateTopic on an existing name hands back that topic, as long as the request asks for
            // nothing it doesn't already have.
            EnsureMatchesExistingTopic(existing, request);
        }

        _cloud.RecordOperation(AwsServiceName.Sns, SnsActionName.CreateTopic, topicArn);
        return Task.FromResult(new CreateTopicResponse
        {
            TopicArn = topicArn
        }.SetCommonProperties());
    }

    /// <summary>
    /// SNS returns an existing topic from <c>CreateTopic</c> only when every attribute and tag the
    /// request names already has the requested value; leaving them out is no difference.
    /// </summary>
    private static void EnsureMatchesExistingTopic(SnsTopicResource existing, CreateTopicRequest request)
    {
        lock (existing.SubscriptionGate)
        {
            foreach (var (name, value) in request.Attributes ?? [])
            {
                var current = existing.Attributes.TryGetValue(name, out var stored) ? stored : null;
                var same = name switch
                {
                    SnsTopicAttributes.FifoTopic => string.Equals(value, existing.IsFifo ? "true" : "false", StringComparison.OrdinalIgnoreCase),
                    SnsTopicAttributes.Policy => current is not null && ResourcePolicy.AreEquivalent(ResourcePolicyKind.Topic, current, value),
                    _ => string.Equals(current ?? string.Empty, value ?? string.Empty, StringComparison.Ordinal),
                };
                if (!same)
                {
                    throw new InternalInvalidParameterException(
                        "Invalid parameter: Attributes Reason: Topic already exists with different attributes");
                }
            }

            if (request.Tags is { Count: > 0 } tags
                && (tags.Count != existing.Tags.Count
                    || tags.Any(t => !existing.Tags.TryGetValue(t.Key, out var v) || !string.Equals(v, t.Value, StringComparison.Ordinal))))
            {
                throw new InternalInvalidParameterException(
                    "Invalid parameter: Tags Reason: Topic already exists with different tags");
            }
        }
    }

    public Task<DeleteTopicResponse> DeleteTopicAsync(DeleteTopicRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        // Deleting a topic that doesn't exist succeeds, as on AWS. Its subscriptions go with it.
        var topicName = SnsArn.TopicName(request.TopicArn);
        if (InThisRegion(request.TopicArn) && _resources.Topics.TryRemove(topicName, out var topic))
        {
            topic.Delete();
        }

        _cloud.RecordOperation(AwsServiceName.Sns, SnsActionName.DeleteTopic, request.TopicArn);
        return Task.FromResult(new DeleteTopicResponse().SetCommonProperties());
    }

    public Task<GetTopicAttributesResponse> GetTopicAttributesAsync(GetTopicAttributesRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var topic = GetTopicByArn(request.TopicArn);
        Authorize(topic, SnsActionName.GetTopicAttributes);

        Dictionary<string, string> attributes;
        lock (topic.SubscriptionGate)
        {
            // A copy, so the caller can't change the topic through the response.
            attributes = new Dictionary<string, string>(topic.Attributes, StringComparer.Ordinal);
        }

        var subscriptions = topic.Subscriptions;
        attributes["TopicArn"] = topic.Arn;
        attributes["Owner"] = _cloud.CurrentAccountId;
        attributes["SubscriptionsConfirmed"] = subscriptions.Count(s => !s.PendingConfirmation).ToString(CultureInfo.InvariantCulture);
        attributes["SubscriptionsPending"] = subscriptions.Count(s => s.PendingConfirmation).ToString(CultureInfo.InvariantCulture);
        attributes["SubscriptionsDeleted"] = topic.SubscriptionsDeleted.ToString(CultureInfo.InvariantCulture);
        attributes[SnsTopicAttributes.EffectiveDeliveryPolicy] = SnsTopicAttributes.EffectiveDeliveryPolicyFor(
            attributes.TryGetValue(SnsTopicAttributes.DeliveryPolicy, out var deliveryPolicy) ? deliveryPolicy : null);

        _cloud.RecordOperation(AwsServiceName.Sns, SnsActionName.GetTopicAttributes, topic.Arn);
        return Task.FromResult(new GetTopicAttributesResponse
        {
            Attributes = attributes
        }.SetCommonProperties());
    }

    public Task<ListTopicsResponse> ListTopicsAsync(ListTopicsRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var (items, nextToken) = SnsPagination.Page(
            _resources.Topics.Values.Select(t => t.Arn),
            request.NextToken);

        _cloud.RecordOperation(AwsServiceName.Sns, SnsActionName.ListTopics);
        return Task.FromResult(new ListTopicsResponse
        {
            Topics = items.Select(arn => new Topic { TopicArn = arn }).ToList(),
            NextToken = nextToken
        }.SetCommonProperties());
    }

    public Task<SetTopicAttributesResponse> SetTopicAttributesAsync(SetTopicAttributesRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var topic = GetTopicByArn(request.TopicArn);
        Authorize(topic, SnsActionName.SetTopicAttributes);
        var name = request.AttributeName;
        if (name is null || !SnsTopicAttributes.IsSettable(name, topic.IsFifo))
        {
            throw new InternalInvalidParameterException("Invalid parameter: AttributeName");
        }

        // Checking the subscriptions and raising the limit have to happen together, or a subscription
        // the new limit forbids can slip in between the two.
        lock (topic.SubscriptionGate)
        {
            if (name.Equals(SnsTopicAttributes.MaximumMessageSize, StringComparison.Ordinal))
            {
                if (ValidateMaximumMessageSize(request.AttributeValue) > SnsTopicResource.DefaultMaximumMessageSize)
                {
                    topic.EnsureSubscriptionsSupportLargeMessages();
                }
            }
            else
            {
                SnsTopicAttributes.Validate(name, request.AttributeValue,
                    topic.Attributes.TryGetValue(name, out var current) ? current : null);
            }

            topic.SetAttribute(name, request.AttributeValue, _cloud.TimeProvider.GetUtcNow());
        }

        _cloud.RecordOperation(AwsServiceName.Sns, SnsActionName.SetTopicAttributes, topic.Arn);
        return Task.FromResult(new SetTopicAttributesResponse().SetCommonProperties());
    }

    // Helper methods

    /// <summary>
    /// Whether an ARN names this client's region. A topic in another region is not found here,
    /// even when this region has one of the same name.
    /// </summary>
    private bool InThisRegion(string? arn) =>
        Arn.RegionOf(arn) is not { } region || string.Equals(region, _resources.Region, StringComparison.Ordinal);

    /// <summary>The topic <paramref name="topicArn"/> names, or SNS's <c>NotFound</c> when there is none.</summary>
    private SnsTopicResource GetTopicByArn(string? topicArn, string parameter = "TopicArn")
    {
        var topicName = SnsArn.TopicName(topicArn, parameter);
        if (!InThisRegion(topicArn))
        {
            // A regional endpoint refuses another region's topic ARN outright, whether or not it exists.
            throw new InternalInvalidParameterException($"Invalid parameter: {parameter}");
        }

        return _resources.Topics.TryGetValue(topicName, out var topic)
            ? topic
            : throw new InternalNotFoundException(TopicDoesNotExist);
    }

    /// <summary>
    /// Checks a <c>MaximumMessageSize</c> attribute value is an integer within the range AWS accepts
    /// (1 KiB to 1 MiB), returning the parsed value. CreateTopic reports the failure through its
    /// attribute map, hence the caller-supplied prefix.
    /// </summary>
    private static int ValidateMaximumMessageSize(string? value, string errorPrefix = "Invalid parameter: ")
    {
        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var size)
            || size < MinimumMaxMessageSize
            || size > LargestMaxMessageSize)
        {
            throw new InternalInvalidParameterException(
                $"{errorPrefix}{SnsTopicAttributes.MaximumMessageSize}: {value} is not an integer between " +
                $"{MinimumMaxMessageSize} and {LargestMaxMessageSize} bytes");
        }

        return size;
    }
}
