#pragma warning disable CS8600, CS8601, CS8602, CS8604 // Nullable reference warnings - internal POCOs use nullable properties but values are set at runtime

using System.Text.Json.Nodes;
using LocalSqsSnsMessaging.Sns.Model;

namespace LocalSqsSnsMessaging;

internal sealed class InternalSnsClient
{
    private readonly InMemoryAwsBus _bus;

    private const int MaxMessageSize = 262144;

    internal InternalSnsClient(InMemoryAwsBus bus)
    {
        _bus = bus;
    }

    public Task<CreateTopicResponse> CreateTopicAsync(CreateTopicRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var topicArn = $"arn:aws:sns:{_bus.CurrentRegion}:{_bus.CurrentAccountId}:{request.Name}";
        var topic = new SnsTopicResource
        {
            Name = request.Name,
            Region = _bus.CurrentRegion,
            Arn = topicArn
        };

        _bus.Topics.TryAdd(request.Name, topic);

        _bus.RecordOperation(AwsServiceName.Sns, SnsActionName.CreateTopic, topicArn);
        return Task.FromResult(new CreateTopicResponse
        {
            TopicArn = topicArn
        }.SetCommonProperties());
    }

    public Task<DeleteTopicResponse> DeleteTopicAsync(DeleteTopicRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var topicName = GetTopicNameByArn(request.TopicArn);
        _bus.Topics.TryRemove(topicName, out _);

        _bus.RecordOperation(AwsServiceName.Sns, SnsActionName.DeleteTopic, request.TopicArn);
        return Task.FromResult(new DeleteTopicResponse().SetCommonProperties());
    }

    public Task<GetSubscriptionAttributesResponse> GetSubscriptionAttributesAsync(
        GetSubscriptionAttributesRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!_bus.Subscriptions.TryGetValue(request.SubscriptionArn, out var subscription))
        {
            throw new NotFoundException("Subscription not found.");
        }

        _bus.RecordOperation(AwsServiceName.Sns, SnsActionName.GetSubscriptionAttributes, subscription.SubscriptionArn);
        return Task.FromResult(new GetSubscriptionAttributesResponse
        {
            Attributes = new Dictionary<string, string>
            {
                ["SubscriptionArn"] = subscription.SubscriptionArn,
                ["TopicArn"] = subscription.TopicArn,
                ["Protocol"] = subscription.Protocol,
                ["Endpoint"] = subscription.EndPoint,
                ["Owner"] = _bus.CurrentAccountId,
                ["ConfirmationWasAuthenticated"] = "false",
                ["IsAuthenticated"] = "false",
                ["PendingConfirmation"] = "false",
                ["RawMessageDelivery"] = subscription.Raw.ToString(),
                ["FilterPolicy"] = subscription.FilterPolicy
            }
        }.SetCommonProperties());
    }

    public Task<GetTopicAttributesResponse> GetTopicAttributesAsync(GetTopicAttributesRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var topicName = GetTopicNameByArn(request.TopicArn);
        if (!_bus.Topics.TryGetValue(topicName, out var topic))
        {
            throw new NotFoundException("Topic not found.");
        }

        // Create a copy of the attributes dictionary to prevent mutation.
        // TODO other default attributes to be added later.
        var attributes = new Dictionary<string, string>(topic.Attributes) {
            ["TopicArn"] = topic.Arn
        };

        _bus.RecordOperation(AwsServiceName.Sns, SnsActionName.GetTopicAttributes, topic.Arn);
        return Task.FromResult(new GetTopicAttributesResponse
        {
            Attributes = attributes
        }.SetCommonProperties());
    }

    public Task<ListSubscriptionsResponse> ListSubscriptionsAsync(ListSubscriptionsRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var allSubscriptions = _bus.Subscriptions.Values
            .Select(s => new Subscription
            {
                SubscriptionArn = s.SubscriptionArn,
                TopicArn = s.TopicArn,
                Protocol = s.Protocol,
                Endpoint = s.EndPoint,
                Owner = _bus.CurrentAccountId,
            }).ToList();

        var pagedSubscriptions = new PaginatedList<Subscription>(allSubscriptions);

        var (items, nextToken) = pagedSubscriptions.GetPage(
            TokenGenerator, 100, request.NextToken);

        _bus.RecordOperation(AwsServiceName.Sns, SnsActionName.ListSubscriptions);
        return Task.FromResult(new ListSubscriptionsResponse
        {
            Subscriptions = items,
            NextToken = nextToken
        }.SetCommonProperties());

        static string TokenGenerator(Subscription x)
        {
            return Convert.ToBase64String(Encoding.UTF8.GetBytes(x.SubscriptionArn));
        }
    }

    public Task<ListSubscriptionsByTopicResponse> ListSubscriptionsByTopicAsync(ListSubscriptionsByTopicRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var topicName = GetTopicNameByArn(request.TopicArn);
        if (!_bus.Topics.TryGetValue(topicName, out _))
        {
            throw new NotFoundException("Topic not found.");
        }

        var allSubscriptions = _bus.Subscriptions.Values
            .Where(s => string.Equals(s.TopicArn, request.TopicArn, StringComparison.OrdinalIgnoreCase))
            .Select(s => new Subscription
            {
                SubscriptionArn = s.SubscriptionArn,
                TopicArn = s.TopicArn,
                Protocol = s.Protocol,
                Endpoint = s.EndPoint,
                Owner = _bus.CurrentAccountId,
            })
            .ToList();

        var pagedSubscriptions = new PaginatedList<Subscription>(allSubscriptions);

        var (items, nextToken) = pagedSubscriptions.GetPage(
            TokenGenerator, 100, request.NextToken);

        _bus.RecordOperation(AwsServiceName.Sns, SnsActionName.ListSubscriptionsByTopic, request.TopicArn);
        return Task.FromResult(new ListSubscriptionsByTopicResponse
        {
            Subscriptions = items,
            NextToken = nextToken
        }.SetCommonProperties());

        static string TokenGenerator(Subscription x)
        {
            return Convert.ToBase64String(Encoding.UTF8.GetBytes(x.SubscriptionArn));
        }
    }

    public Task<ListTagsForResourceResponse> ListTagsForResourceAsync(ListTagsForResourceRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var topicName = GetTopicNameByArn(request.ResourceArn);
        if (!_bus.Topics.TryGetValue(topicName, out var topic))
        {
            throw new ResourceNotFoundException("Topic not found.");
        }

        var tags = topic.Tags.Select(t => new Tag { Key = t.Key, Value = t.Value }).ToList();

        _bus.RecordOperation(AwsServiceName.Sns, SnsActionName.ListTagsForResource, request.ResourceArn);
        return Task.FromResult(new ListTagsForResourceResponse
        {
            Tags = tags
        }.SetCommonProperties());
    }

    public Task<ListTopicsResponse> ListTopicsAsync(ListTopicsRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var allTopics = _bus.Topics.Values
            .Select(t => new Topic { TopicArn = t.Arn })
            .ToList();

        var pagedTopics = new PaginatedList<Topic>(allTopics);

        var (items, nextToken) = pagedTopics.GetPage(
            TokenGenerator, 100, request.NextToken);

        _bus.RecordOperation(AwsServiceName.Sns, SnsActionName.ListTopics);
        return Task.FromResult(new ListTopicsResponse
        {
            Topics = items,
            NextToken = nextToken
        }.SetCommonProperties());

        static string TokenGenerator(Topic x)
        {
            return Convert.ToBase64String(Encoding.UTF8.GetBytes(x.TopicArn));
        }
    }

    public Task<PublishResponse> PublishAsync(PublishRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var messageSize = CalculateMessageSize(request.Message, request.Subject, request.MessageAttributes);
        if (messageSize > MaxMessageSize)
        {
            throw new InvalidParameterException($"Message size has exceeded the limit of {MaxMessageSize} bytes.");
        }

        var topic = GetTopicByArn(request.TopicArn);
        var result = topic.PublishAction.Execute(request);

        _bus.RecordOperation(AwsServiceName.Sns, SnsActionName.Publish, request.TopicArn);
        return Task.FromResult(result);
    }

    public Task<PublishBatchResponse> PublishBatchAsync(PublishBatchRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var totalSize = request.PublishBatchRequestEntries
            .Sum(requestEntry => CalculateMessageSize(requestEntry.Message, requestEntry.Subject, requestEntry.MessageAttributes));
        if (totalSize > MaxMessageSize)
        {
            throw new BatchRequestTooLongException(
                $"Batch size ({totalSize} bytes) exceeds the maximum allowed size ({MaxMessageSize} bytes)");
        }

        var topic = GetTopicByArn(request.TopicArn);
        var result = topic.PublishAction.ExecuteBatch(request);

        _bus.RecordOperation(AwsServiceName.Sns, SnsActionName.PublishBatch, request.TopicArn);
        return Task.FromResult(result);
    }

    public Task<RemovePermissionResponse> RemovePermissionAsync(RemovePermissionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var topic = GetTopicByArn(request.TopicArn);

        if (!topic.Attributes.TryGetValue("Policy", out var policyJson))
        {
            throw new ArgumentException($"Value {request.Label} for parameter Label is invalid. Reason: can't find label.");
        }

        var policy = JsonNode.Parse(policyJson)!.AsObject();
        var statements = policy["Statement"]!.AsArray();

        int? indexToRemove = null;
        for (int i = 0; i < statements.Count; i++)
        {
            if (statements[i]?["Sid"]?.GetValue<string>() == request.Label)
            {
                indexToRemove = i;
                break;
            }
        }

        if (indexToRemove is null)
        {
            throw new ArgumentException($"Value {request.Label} for parameter Label is invalid. Reason: can't find label.");
        }

        statements.RemoveAt(indexToRemove.Value);

        if (statements.Count > 0)
        {
            topic.Attributes["Policy"] = policy.ToJsonString();
        }
        else
        {
            topic.Attributes.Remove("Policy");
        }

        _bus.RecordOperation(AwsServiceName.Sns, SnsActionName.RemovePermission, topic.Arn);
        return Task.FromResult(new RemovePermissionResponse().SetCommonProperties());
    }

    public Task<SetSubscriptionAttributesResponse> SetSubscriptionAttributesAsync(
        SetSubscriptionAttributesRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!_bus.Subscriptions.TryGetValue(request.SubscriptionArn, out var subscription))
        {
            throw new NotFoundException($"Subscription not found: {request.SubscriptionArn}");
        }

        // Update the attribute
        if (request.AttributeName.Equals("RawMessageDelivery", StringComparison.OrdinalIgnoreCase))
        {
            if (bool.TryParse(request.AttributeValue, out var isRawMessageDelivery))
            {
                subscription.Raw = isRawMessageDelivery;
            }
            else
            {
                throw new InvalidParameterException(
                    "Invalid value for RawMessageDelivery attribute. Expected true or false.");
            }
        }
        else if (request.AttributeName.Equals("FilterPolicy", StringComparison.OrdinalIgnoreCase))
        {
            subscription.FilterPolicy = request.AttributeValue;
        }
        else
        {
            throw new InvalidParameterException($"Unsupported attribute: {request.AttributeName}");
        }

        _bus.RecordOperation(AwsServiceName.Sns, SnsActionName.SetSubscriptionAttributes, subscription.SubscriptionArn);
        return Task.FromResult(new SetSubscriptionAttributesResponse().SetCommonProperties());
    }

    public Task<SetTopicAttributesResponse> SetTopicAttributesAsync(SetTopicAttributesRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var topicName = GetTopicNameByArn(request.TopicArn);
        if (!_bus.Topics.TryGetValue(topicName, out var topic))
        {
            throw new NotFoundException("Topic not found.");
        }

        topic.Attributes[request.AttributeName] = request.AttributeValue;
        _bus.RecordOperation(AwsServiceName.Sns, SnsActionName.SetTopicAttributes, topic.Arn);
        return Task.FromResult(new SetTopicAttributesResponse().SetCommonProperties());
    }

    public Task<SubscribeResponse> SubscribeAsync(SubscribeRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!request.Protocol.Equals("sqs", StringComparison.OrdinalIgnoreCase))
        {
            throw new NotSupportedException("Only SQS protocol is supported.");
        }

        var queueName = request.Endpoint.Split(':').Last();
        if (!_bus.Queues.TryGetValue(queueName, out _))
        {
            throw new NotFoundException("Queue not found.");
        }

        var parsedRawMessageDelivery =
            request.Attributes?.TryGetValue("RawMessageDelivery", out var rawMessageDelivery) == true &&
            bool.TryParse(rawMessageDelivery, out var isRawMessageDelivery) &&
            isRawMessageDelivery;

        var parsedFilterPolicy =
            request.Attributes?.TryGetValue("FilterPolicy", out var filterPolicy) == true ? filterPolicy : string.Empty;

        var snsSubscription = new SnsSubscription
        {
            SubscriptionArn = Guid.NewGuid().ToString(),
            TopicArn = request.TopicArn,
            EndPoint = request.Endpoint,
            Protocol = request.Protocol,
            Raw = parsedRawMessageDelivery,
            FilterPolicy = parsedFilterPolicy
        };
        _bus.Subscriptions.TryAdd(snsSubscription.SubscriptionArn, snsSubscription);

        SnsPublishActionFactory.UpdateTopicPublishAction(snsSubscription.TopicArn, _bus);

        _bus.RecordOperation(AwsServiceName.Sns, SnsActionName.Subscribe, request.TopicArn);
        return Task.FromResult(new SubscribeResponse
        {
            SubscriptionArn = snsSubscription.SubscriptionArn
        }.SetCommonProperties());
    }

    public Task<TagResourceResponse> TagResourceAsync(TagResourceRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var topicName = GetTopicNameByArn(request.ResourceArn);
        if (!_bus.Topics.TryGetValue(topicName, out var topic))
        {
            throw new ResourceNotFoundException("Topic not found.");
        }

        foreach (var tag in request.Tags)
        {
            topic.Tags[tag.Key] = tag.Value;
        }

        _bus.RecordOperation(AwsServiceName.Sns, SnsActionName.TagResource, request.ResourceArn);
        return Task.FromResult(new TagResourceResponse().SetCommonProperties());
    }

    public Task<UnsubscribeResponse> UnsubscribeAsync(UnsubscribeRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!_bus.Subscriptions.TryRemove(request.SubscriptionArn, out var subscription))
        {
            throw new NotFoundException("Subscription not found.");
        }

        SnsPublishActionFactory.UpdateTopicPublishAction(subscription.TopicArn, _bus);

        _bus.RecordOperation(AwsServiceName.Sns, SnsActionName.Unsubscribe, request.SubscriptionArn);
        return Task.FromResult(new UnsubscribeResponse().SetCommonProperties());
    }

    public Task<UntagResourceResponse> UntagResourceAsync(UntagResourceRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var topicName = GetTopicNameByArn(request.ResourceArn);
        if (!_bus.Topics.TryGetValue(topicName, out var topic))
        {
            throw new ResourceNotFoundException("Topic not found.");
        }

        foreach (var tagKey in request.TagKeys)
        {
            topic.Tags.Remove(tagKey);
        }

        _bus.RecordOperation(AwsServiceName.Sns, SnsActionName.UntagResource, request.ResourceArn);
        return Task.FromResult(new UntagResourceResponse().SetCommonProperties());
    }

    public Task<AddPermissionResponse> AddPermissionAsync(AddPermissionRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var topic = GetTopicByArn(request.TopicArn);

        JsonObject policy;
        if (topic.Attributes.TryGetValue("Policy", out var policyJson))
        {
            policy = JsonNode.Parse(policyJson)!.AsObject();
        }
        else
        {
            policy = new JsonObject
            {
                ["Version"] = "2012-10-17",
                ["Id"] = $"{topic.Arn}/SNSDefaultPolicy",
                ["Statement"] = new JsonArray()
            };
        }

        var statements = policy["Statement"]!.AsArray();

        foreach (var stmt in statements)
        {
            if (stmt?["Sid"]?.GetValue<string>() == request.Label)
            {
                throw new ArgumentException($"Value {request.Label} for parameter Label is invalid. Reason: Already exists.");
            }
        }

        var principals = new JsonArray();
        foreach (var accountId in request.AWSAccountId ?? [])
        {
            principals.Add($"arn:aws:iam::{accountId}:root");
        }

        var actions = new JsonArray();
        foreach (var action in request.ActionName ?? [])
        {
            actions.Add($"SNS:{action}");
        }

        var newStatement = new JsonObject
        {
            ["Sid"] = request.Label,
            ["Effect"] = "Allow",
            ["Principal"] = new JsonObject { ["AWS"] = principals },
            ["Action"] = actions,
            ["Resource"] = topic.Arn
        };

        statements.Add(newStatement);
        topic.Attributes["Policy"] = policy.ToJsonString();

        _bus.RecordOperation(AwsServiceName.Sns, SnsActionName.AddPermission, topic.Arn);
        return Task.FromResult(new AddPermissionResponse().SetCommonProperties());
    }

    // Stub methods for unsupported operations

    public Task<CheckIfPhoneNumberIsOptedOutResponse> CheckIfPhoneNumberIsOptedOutAsync(CheckIfPhoneNumberIsOptedOutRequest request, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("CheckIfPhoneNumberIsOptedOut is not supported.");

    public Task<ConfirmSubscriptionResponse> ConfirmSubscriptionAsync(ConfirmSubscriptionRequest request, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("ConfirmSubscription is not supported.");

    public Task<CreatePlatformApplicationResponse> CreatePlatformApplicationAsync(CreatePlatformApplicationRequest request, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("CreatePlatformApplication is not supported.");

    public Task<CreatePlatformEndpointResponse> CreatePlatformEndpointAsync(CreatePlatformEndpointRequest request, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("CreatePlatformEndpoint is not supported.");

    public Task<CreateSMSSandboxPhoneNumberResponse> CreateSMSSandboxPhoneNumberAsync(CreateSMSSandboxPhoneNumberRequest request, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("CreateSMSSandboxPhoneNumber is not supported.");

    public Task<DeleteEndpointResponse> DeleteEndpointAsync(DeleteEndpointRequest request, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("DeleteEndpoint is not supported.");

    public Task<DeletePlatformApplicationResponse> DeletePlatformApplicationAsync(DeletePlatformApplicationRequest request, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("DeletePlatformApplication is not supported.");

    public Task<DeleteSMSSandboxPhoneNumberResponse> DeleteSMSSandboxPhoneNumberAsync(DeleteSMSSandboxPhoneNumberRequest request, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("DeleteSMSSandboxPhoneNumber is not supported.");

    public Task<GetDataProtectionPolicyResponse> GetDataProtectionPolicyAsync(GetDataProtectionPolicyRequest request, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("GetDataProtectionPolicy is not supported.");

    public Task<GetEndpointAttributesResponse> GetEndpointAttributesAsync(GetEndpointAttributesRequest request, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("GetEndpointAttributes is not supported.");

    public Task<GetPlatformApplicationAttributesResponse> GetPlatformApplicationAttributesAsync(GetPlatformApplicationAttributesRequest request, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("GetPlatformApplicationAttributes is not supported.");

    public Task<GetSMSAttributesResponse> GetSMSAttributesAsync(GetSMSAttributesRequest request, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("GetSMSAttributes is not supported.");

    public Task<GetSMSSandboxAccountStatusResponse> GetSMSSandboxAccountStatusAsync(GetSMSSandboxAccountStatusRequest request, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("GetSMSSandboxAccountStatus is not supported.");

    public Task<ListEndpointsByPlatformApplicationResponse> ListEndpointsByPlatformApplicationAsync(ListEndpointsByPlatformApplicationRequest request, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("ListEndpointsByPlatformApplication is not supported.");

    public Task<ListOriginationNumbersResponse> ListOriginationNumbersAsync(ListOriginationNumbersRequest request, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("ListOriginationNumbers is not supported.");

    public Task<ListPhoneNumbersOptedOutResponse> ListPhoneNumbersOptedOutAsync(ListPhoneNumbersOptedOutRequest request, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("ListPhoneNumbersOptedOut is not supported.");

    public Task<ListPlatformApplicationsResponse> ListPlatformApplicationsAsync(ListPlatformApplicationsRequest request, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("ListPlatformApplications is not supported.");

    public Task<ListSMSSandboxPhoneNumbersResponse> ListSMSSandboxPhoneNumbersAsync(ListSMSSandboxPhoneNumbersRequest request, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("ListSMSSandboxPhoneNumbers is not supported.");

    public Task<OptInPhoneNumberResponse> OptInPhoneNumberAsync(OptInPhoneNumberRequest request, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("OptInPhoneNumber is not supported.");

    public Task<PutDataProtectionPolicyResponse> PutDataProtectionPolicyAsync(PutDataProtectionPolicyRequest request, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("PutDataProtectionPolicy is not supported.");

    public Task<SetEndpointAttributesResponse> SetEndpointAttributesAsync(SetEndpointAttributesRequest request, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("SetEndpointAttributes is not supported.");

    public Task<SetPlatformApplicationAttributesResponse> SetPlatformApplicationAttributesAsync(SetPlatformApplicationAttributesRequest request, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("SetPlatformApplicationAttributes is not supported.");

    public Task<SetSMSAttributesResponse> SetSMSAttributesAsync(SetSMSAttributesRequest request, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("SetSMSAttributes is not supported.");

    public Task<VerifySMSSandboxPhoneNumberResponse> VerifySMSSandboxPhoneNumberAsync(VerifySMSSandboxPhoneNumberRequest request, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("VerifySMSSandboxPhoneNumber is not supported.");

    // Helper methods

    private SnsTopicResource GetTopicByArn(string topicArn)
    {
        var topicName = GetTopicNameByArn(topicArn);
        return _bus.Topics.TryGetValue(topicName, out var topic)
            ? topic
            : throw new NotFoundException($"Topic not found: {topicArn}");
    }

    private static string GetTopicNameByArn(string topicArn)
    {
        var indexOfLastColon = topicArn.LastIndexOf(':');
        if (indexOfLastColon == -1)
        {
            throw new ArgumentException("ARN malformed", nameof(topicArn));
        }
        return topicArn[(indexOfLastColon+1) ..];
    }

    private static int CalculateMessageSize(string message, string? subject, Dictionary<string, MessageAttributeValue>? messageAttributes)
    {
        var totalSize = 0;

        // Add message body size
        totalSize += Encoding.UTF8.GetByteCount(message);

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
                totalSize += Encoding.UTF8.GetByteCount(attributeValue.DataType);

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
