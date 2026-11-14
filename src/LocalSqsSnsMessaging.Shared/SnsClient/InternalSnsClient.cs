#pragma warning disable CS8600, CS8601, CS8602, CS8604 // Nullable reference warnings - internal POCOs use nullable properties but values are set at runtime

using LocalSqsSnsMessaging.Sns.Model;

namespace LocalSqsSnsMessaging;

/// <summary>
/// The in-memory SNS service. Topic operations are here; subscriptions, publishing, permissions and
/// tags, and the operations this bus doesn't support are in the other parts of the class.
/// </summary>
internal sealed partial class InternalSnsClient
{
    private readonly InMemoryAwsBus _bus;

    /// <summary>The message size a topic accepts when it hasn't set <c>MaximumMessageSize</c>: 256 KiB.</summary>
    private const int DefaultMaxMessageSize = 262144;

    /// <summary>The smallest value <c>MaximumMessageSize</c> can be set to: 1 KiB.</summary>
    private const int MinimumMaxMessageSize = 1024;

    /// <summary>The largest value <c>MaximumMessageSize</c> can be set to: 1 MiB.</summary>
    private const int LargestMaxMessageSize = 1048576;

    /// <summary>A topic accepting more than 256 KiB refuses subscriptions beyond this many.</summary>
    private const int LargeMessageSubscriptionLimit = 100;

    /// <summary>SNS answers every request for a topic that doesn't exist with this message.</summary>
    internal const string TopicDoesNotExist = "Topic does not exist";

    internal InternalSnsClient(InMemoryAwsBus bus)
    {
        _bus = bus;
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

        var topicArn = $"arn:aws:sns:{_bus.CurrentRegion}:{_bus.CurrentAccountId}:{request.Name}";
        var topic = new SnsTopicResource
        {
            Name = request.Name,
            Region = _bus.CurrentRegion,
            Arn = topicArn,
            Fifo = fifo ? new SnsFifoTopicState() : null,
        };
        topic.PublishAction = new SnsPublishAction([], topic, _bus);
        topic.Attributes[SnsTopicAttributes.DisplayName] = string.Empty;
        topic.Attributes[SnsTopicAttributes.Policy] = SnsTopicAttributes.DefaultPolicy(topicArn, _bus.CurrentAccountId);
        if (fifo)
        {
            topic.Attributes[SnsTopicAttributes.FifoTopic] = "true";
            topic.Attributes[SnsTopicAttributes.ContentBasedDeduplication] = "false";
        }

        var now = _bus.TimeProvider.GetUtcNow();
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

        var existing = _bus.Topics.GetOrAdd(request.Name, topic);
        if (!ReferenceEquals(existing, topic))
        {
            // CreateTopic on an existing name hands back that topic, as long as the request asks for
            // nothing it doesn't already have.
            EnsureMatchesExistingTopic(existing, request);
        }

        _bus.RecordOperation(AwsServiceName.Sns, SnsActionName.CreateTopic, topicArn);
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
                    SnsTopicAttributes.Policy => current is not null && SnsPolicyDocument.AreEquivalent(current, value),
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
        if (_bus.Topics.TryRemove(topicName, out var topic))
        {
            lock (topic.SubscriptionGate)
            {
                foreach (var subscription in _bus.Subscriptions.Values.Where(s => string.Equals(s.TopicArn, topic.Arn, StringComparison.Ordinal)).ToList())
                {
                    _bus.Subscriptions.TryRemove(subscription.SubscriptionArn, out _);
                }
            }
        }

        _bus.RecordOperation(AwsServiceName.Sns, SnsActionName.DeleteTopic, request.TopicArn);
        return Task.FromResult(new DeleteTopicResponse().SetCommonProperties());
    }

    public Task<GetTopicAttributesResponse> GetTopicAttributesAsync(GetTopicAttributesRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var topic = GetTopicByArn(request.TopicArn);

        Dictionary<string, string> attributes;
        lock (topic.SubscriptionGate)
        {
            // A copy, so the caller can't change the topic through the response.
            attributes = new Dictionary<string, string>(topic.Attributes, StringComparer.Ordinal);
        }

        var subscriptions = _bus.Subscriptions.Values
            .Where(s => string.Equals(s.TopicArn, topic.Arn, StringComparison.Ordinal))
            .ToList();
        attributes["TopicArn"] = topic.Arn;
        attributes["Owner"] = _bus.CurrentAccountId;
        attributes["SubscriptionsConfirmed"] = subscriptions.Count(s => !s.PendingConfirmation).ToString(CultureInfo.InvariantCulture);
        attributes["SubscriptionsPending"] = subscriptions.Count(s => s.PendingConfirmation).ToString(CultureInfo.InvariantCulture);
        attributes["SubscriptionsDeleted"] = topic.SubscriptionsDeleted.ToString(CultureInfo.InvariantCulture);
        attributes[SnsTopicAttributes.EffectiveDeliveryPolicy] = SnsTopicAttributes.EffectiveDeliveryPolicyFor(
            attributes.TryGetValue(SnsTopicAttributes.DeliveryPolicy, out var deliveryPolicy) ? deliveryPolicy : null);

        _bus.RecordOperation(AwsServiceName.Sns, SnsActionName.GetTopicAttributes, topic.Arn);
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
            _bus.Topics.Values.Select(t => t.Arn),
            request.NextToken);

        _bus.RecordOperation(AwsServiceName.Sns, SnsActionName.ListTopics);
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
                if (ValidateMaximumMessageSize(request.AttributeValue) > DefaultMaxMessageSize)
                {
                    ValidateSubscriptionsSupportLargeMessages(topic);
                }
            }
            else
            {
                SnsTopicAttributes.Validate(name, request.AttributeValue,
                    topic.Attributes.TryGetValue(name, out var current) ? current : null);
            }

            topic.SetAttribute(name, request.AttributeValue, _bus.TimeProvider.GetUtcNow());
        }

        _bus.RecordOperation(AwsServiceName.Sns, SnsActionName.SetTopicAttributes, topic.Arn);
        return Task.FromResult(new SetTopicAttributesResponse().SetCommonProperties());
    }

    // Helper methods

    /// <summary>The topic <paramref name="topicArn"/> names, or SNS's <c>NotFound</c> when there is none.</summary>
    private SnsTopicResource GetTopicByArn(string? topicArn, string parameter = "TopicArn")
    {
        var topicName = SnsArn.TopicName(topicArn, parameter);
        return _bus.Topics.TryGetValue(topicName, out var topic)
            ? topic
            : throw new InternalNotFoundException(TopicDoesNotExist);
    }

    /// <summary>
    /// The topic's effective message size limit: its <c>MaximumMessageSize</c> attribute, or 256 KiB
    /// when the topic hasn't opted in to larger payloads.
    /// </summary>
    private static int GetMaxMessageSize(SnsTopicResource topic)
    {
        lock (topic.SubscriptionGate)
        {
            return topic.Attributes.TryGetValue(SnsTopicAttributes.MaximumMessageSize, out var value)
                   && int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var size)
                ? size
                : DefaultMaxMessageSize;
        }
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

    /// <summary>
    /// A topic that accepts more than 256 KiB only supports SQS subscriptions (AWS also allows
    /// Firehose and Lambda, neither of which this bus supports). AWS checks only the protocols
    /// here - raising the limit on a topic that already has more than 100 subscriptions is allowed,
    /// and only the next <c>Subscribe</c> is turned away.
    /// </summary>
    private void ValidateSubscriptionsSupportLargeMessages(SnsTopicResource topic)
    {
        // Unconfirmed HTTP/S subscriptions count too, as on AWS.
        var unsupported = _bus.Subscriptions.Values.FirstOrDefault(s => s.TopicArn == topic.Arn && !s.IsSqs);
        if (unsupported is not null)
        {
            throw new InternalInvalidParameterException(UnsupportedProtocolMessage(unsupported.Protocol));
        }
    }

    private static string UnsupportedProtocolMessage(string protocol) =>
        $"Invalid parameter: {SnsTopicAttributes.MaximumMessageSize} greater than {DefaultMaxMessageSize} bytes " +
        $"is not supported for the following protocol: [{protocol}]";

    /// <summary>
    /// The subscription side of the same constraint: a topic that accepts more than 256 KiB can't take
    /// an HTTP/S subscription, or a subscription beyond its hundredth.
    /// </summary>
    private void ValidateSubscriptionSupportsTopicMessageSize(SnsTopicResource topic, string protocol)
    {
        if (GetMaxMessageSize(topic) <= DefaultMaxMessageSize)
        {
            return;
        }

        if (protocol is "http" or "https")
        {
            throw new InternalInvalidParameterException(UnsupportedProtocolMessage(protocol));
        }

        if (_bus.Subscriptions.Values.Count(s => s.TopicArn == topic.Arn) >= LargeMessageSubscriptionLimit)
        {
            throw new InternalInvalidParameterException(
                $"Invalid parameter: A topic with {SnsTopicAttributes.MaximumMessageSize} greater than {DefaultMaxMessageSize} bytes " +
                $"supports a maximum of {LargeMessageSubscriptionLimit} subscriptions");
        }
    }
}
