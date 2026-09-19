#pragma warning disable CS8600, CS8601, CS8602, CS8604 // Nullable reference warnings - internal POCOs use nullable properties but values are set at runtime

using System.Text.Json.Nodes;
using LocalSqsSnsMessaging.Sns.Model;

namespace LocalSqsSnsMessaging;

internal sealed class InternalSnsClient
{
    private readonly InMemoryAwsBus _bus;

    /// <summary>The name of the topic attribute that opts a topic in to payloads above 256 KiB.</summary>
    private const string MaximumMessageSizeAttribute = "MaximumMessageSize";

    /// <summary>The message size a topic accepts when it hasn't set <c>MaximumMessageSize</c>: 256 KiB.</summary>
    private const int DefaultMaxMessageSize = 262144;

    /// <summary>The smallest value <c>MaximumMessageSize</c> can be set to: 1 KiB.</summary>
    private const int MinimumMaxMessageSize = 1024;

    /// <summary>The largest value <c>MaximumMessageSize</c> can be set to: 1 MiB.</summary>
    private const int LargestMaxMessageSize = 1048576;

    /// <summary>A topic accepting more than 256 KiB refuses subscriptions beyond this many.</summary>
    private const int LargeMessageSubscriptionLimit = 100;

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

        foreach (var (name, value) in request.Attributes ?? [])
        {
            if (name.Equals(MaximumMessageSizeAttribute, StringComparison.OrdinalIgnoreCase))
            {
                // A brand new topic has no subscriptions, so only the value itself needs checking.
                ValidateMaximumMessageSize(value, "Invalid parameter: Attributes Reason: ");
            }
            else if (name.Equals("DeliveryPolicy", StringComparison.OrdinalIgnoreCase))
            {
                SnsDeliveryPolicy.Validate(value);
            }

            topic.Attributes[name] = value;
        }

        // Attributes only apply to a topic we actually create; CreateTopic on an existing topic
        // hands back the topic as it stands, as on AWS.
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
            throw new InternalNotFoundException("Subscription not found.");
        }

        var attributes = new Dictionary<string, string>
        {
            ["SubscriptionArn"] = subscription.SubscriptionArn,
            ["TopicArn"] = subscription.TopicArn,
            ["Protocol"] = subscription.Protocol,
            ["Endpoint"] = subscription.EndPoint,
            ["Owner"] = _bus.CurrentAccountId,
            ["ConfirmationWasAuthenticated"] = "false",
            ["IsAuthenticated"] = "false",
            ["PendingConfirmation"] = subscription.PendingConfirmation ? "true" : "false",
            ["RawMessageDelivery"] = subscription.Raw.ToString(),
            ["FilterPolicy"] = subscription.FilterPolicy
        };

        if (!string.IsNullOrEmpty(subscription.FilterPolicy))
        {
            attributes["FilterPolicyScope"] = subscription.FilterPolicyScope;
        }
        if (subscription.RedrivePolicy is not null)
        {
            attributes["RedrivePolicy"] = subscription.RedrivePolicy;
        }
        if (subscription.DeliveryPolicy is not null)
        {
            attributes["DeliveryPolicy"] = subscription.DeliveryPolicy;
        }

        _bus.RecordOperation(AwsServiceName.Sns, SnsActionName.GetSubscriptionAttributes, subscription.SubscriptionArn);
        return Task.FromResult(new GetSubscriptionAttributesResponse
        {
            Attributes = attributes
        }.SetCommonProperties());
    }

    public Task<GetTopicAttributesResponse> GetTopicAttributesAsync(GetTopicAttributesRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var topicName = GetTopicNameByArn(request.TopicArn);
        if (!_bus.Topics.TryGetValue(topicName, out var topic))
        {
            throw new InternalNotFoundException("Topic not found.");
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
                SubscriptionArn = s.PendingConfirmation ? "PendingConfirmation" : s.SubscriptionArn,
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
            throw new InternalNotFoundException("Topic not found.");
        }

        var allSubscriptions = _bus.Subscriptions.Values
            .Where(s => string.Equals(s.TopicArn, request.TopicArn, StringComparison.OrdinalIgnoreCase))
            .Select(s => new Subscription
            {
                SubscriptionArn = s.PendingConfirmation ? "PendingConfirmation" : s.SubscriptionArn,
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
            throw new InternalResourceNotFoundException("Topic not found.");
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
        var totalSize = request.PublishBatchRequestEntries
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
            throw new InternalNotFoundException($"Subscription not found: {request.SubscriptionArn}");
        }

        ApplySubscriptionAttribute(subscription, request.AttributeName, request.AttributeValue, strict: true);

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
            throw new InternalNotFoundException("Topic not found.");
        }

        if (request.AttributeName.Equals("DeliveryPolicy", StringComparison.OrdinalIgnoreCase))
        {
            SnsDeliveryPolicy.Validate(request.AttributeValue);
        }
        else if (request.AttributeName.Equals(MaximumMessageSizeAttribute, StringComparison.OrdinalIgnoreCase))
        {
            var size = ValidateMaximumMessageSize(request.AttributeValue);
            if (size > DefaultMaxMessageSize)
            {
                ValidateSubscriptionsSupportLargeMessages(topic);
            }
        }

        topic.Attributes[request.AttributeName] = request.AttributeValue;
        _bus.RecordOperation(AwsServiceName.Sns, SnsActionName.SetTopicAttributes, topic.Arn);
        return Task.FromResult(new SetTopicAttributesResponse().SetCommonProperties());
    }

    public Task<SubscribeResponse> SubscribeAsync(SubscribeRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var protocol = NormalizeProtocol(request.Protocol);
        switch (protocol)
        {
            case "sqs":
                var queueName = request.Endpoint.Split(':').Last();
                if (!_bus.Queues.TryGetValue(queueName, out _))
                {
                    throw new InternalNotFoundException("Queue not found.");
                }
                break;

            case "http":
            case "https":
                if (!Uri.TryCreate(request.Endpoint, UriKind.Absolute, out var endpointUri) ||
                    !string.Equals(endpointUri.Scheme, protocol, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InternalInvalidParameterException(
                        $"Invalid parameter: Endpoint: Endpoint must be a valid {protocol} URL");
                }
                break;

            default:
                throw new NotSupportedException("Only the sqs, http and https protocols are supported.");
        }

        ValidateSubscriptionSupportsTopicMessageSize(request.TopicArn, protocol);

        var snsSubscription = new SnsSubscription
        {
            SubscriptionArn = Guid.NewGuid().ToString(),
            TopicArn = request.TopicArn,
            EndPoint = request.Endpoint,
            Protocol = protocol,
            Raw = false,
            FilterPolicy = string.Empty,
            // HTTP/S endpoints have to confirm before they receive anything, as on AWS.
            PendingConfirmation = protocol is "http" or "https"
        };

        // Apply the scope before the policy so a nested (body-scoped) policy validates correctly.
        if (request.Attributes is not null)
        {
            if (request.Attributes.TryGetValue("FilterPolicyScope", out var scope))
            {
                ApplySubscriptionAttribute(snsSubscription, "FilterPolicyScope", scope, strict: false);
            }
            foreach (var (name, value) in request.Attributes)
            {
                if (!name.Equals("FilterPolicyScope", StringComparison.OrdinalIgnoreCase))
                {
                    ApplySubscriptionAttribute(snsSubscription, name, value, strict: false);
                }
            }
        }

        _bus.Subscriptions.TryAdd(snsSubscription.SubscriptionArn, snsSubscription);

        SnsPublishActionFactory.UpdateTopicPublishAction(snsSubscription.TopicArn, _bus);

        if (snsSubscription.IsHttp)
        {
            SnsHttpDelivery.SendSubscriptionConfirmation(_bus, snsSubscription);
        }

        _bus.RecordOperation(AwsServiceName.Sns, SnsActionName.Subscribe, request.TopicArn);
        return Task.FromResult(new SubscribeResponse
        {
            // AWS only hands back the ARN of an unconfirmed subscription when asked to.
            SubscriptionArn = snsSubscription.PendingConfirmation && request.ReturnSubscriptionArn != true
                ? "pending confirmation"
                : snsSubscription.SubscriptionArn
        }.SetCommonProperties());
    }

    public Task<TagResourceResponse> TagResourceAsync(TagResourceRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var topicName = GetTopicNameByArn(request.ResourceArn);
        if (!_bus.Topics.TryGetValue(topicName, out var topic))
        {
            throw new InternalResourceNotFoundException("Topic not found.");
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
            throw new InternalNotFoundException("Subscription not found.");
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
            throw new InternalResourceNotFoundException("Topic not found.");
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
            principals.Add((JsonNode?)$"arn:aws:iam::{accountId}:root");
        }

        var actions = new JsonArray();
        foreach (var action in request.ActionName ?? [])
        {
            actions.Add((JsonNode?)$"SNS:{action}");
        }

        var newStatement = new JsonObject
        {
            ["Sid"] = request.Label,
            ["Effect"] = "Allow",
            ["Principal"] = new JsonObject { ["AWS"] = principals },
            ["Action"] = actions,
            ["Resource"] = topic.Arn
        };

        statements.Add((JsonNode)newStatement);
        topic.Attributes["Policy"] = policy.ToJsonString();

        _bus.RecordOperation(AwsServiceName.Sns, SnsActionName.AddPermission, topic.Arn);
        return Task.FromResult(new AddPermissionResponse().SetCommonProperties());
    }

    // Stub methods for unsupported operations

    public Task<CheckIfPhoneNumberIsOptedOutResponse> CheckIfPhoneNumberIsOptedOutAsync(CheckIfPhoneNumberIsOptedOutRequest request, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("CheckIfPhoneNumberIsOptedOut is not supported.");

    public Task<ConfirmSubscriptionResponse> ConfirmSubscriptionAsync(ConfirmSubscriptionRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var subscription = _bus.Subscriptions.Values.FirstOrDefault(s =>
            string.Equals(s.TopicArn, request.TopicArn, StringComparison.Ordinal) &&
            string.Equals(s.ConfirmationToken, request.Token, StringComparison.Ordinal));

        if (subscription is null)
        {
            throw new InternalInvalidParameterException("Invalid parameter: Token");
        }

        if (subscription.PendingConfirmation)
        {
            subscription.PendingConfirmation = false;
            SnsPublishActionFactory.UpdateTopicPublishAction(subscription.TopicArn, _bus);
        }

        _bus.RecordOperation(AwsServiceName.Sns, SnsActionName.ConfirmSubscription, subscription.SubscriptionArn);
        return Task.FromResult(new ConfirmSubscriptionResponse
        {
            SubscriptionArn = subscription.SubscriptionArn
        }.SetCommonProperties());
    }

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

    /// <summary>
    /// Validates and applies a single subscription attribute. <paramref name="strict"/> controls
    /// whether unknown attribute names are rejected (SetSubscriptionAttributes) or ignored (Subscribe,
    /// where callers commonly pass through attributes we don't model).
    /// </summary>
    private static void ApplySubscriptionAttribute(SnsSubscription subscription, string name, string? value, bool strict)
    {
        value ??= string.Empty;

        if (name.Equals("RawMessageDelivery", StringComparison.OrdinalIgnoreCase))
        {
            if (!bool.TryParse(value, out var isRawMessageDelivery))
            {
                throw new InternalInvalidParameterException(
                    "Invalid value for RawMessageDelivery attribute. Expected true or false.");
            }
            subscription.Raw = isRawMessageDelivery;
        }
        else if (name.Equals("FilterPolicy", StringComparison.OrdinalIgnoreCase))
        {
            SnsFilterPolicy.Validate(value, subscription.FilterPolicyScope);
            subscription.FilterPolicy = value;
        }
        else if (name.Equals("FilterPolicyScope", StringComparison.OrdinalIgnoreCase))
        {
            if (!SnsFilterPolicy.IsValidScope(value))
            {
                throw new InternalInvalidParameterException(
                    "Invalid parameter: FilterPolicyScope: Valid values are MessageAttributes and MessageBody");
            }
            SnsFilterPolicy.Validate(subscription.FilterPolicy, value);
            subscription.FilterPolicyScope = value;
        }
        else if (name.Equals("RedrivePolicy", StringComparison.OrdinalIgnoreCase))
        {
            var deadLetterTargetArn = SnsRedrivePolicy.ParseDeadLetterTargetArn(value);
            subscription.DeadLetterTargetArn = deadLetterTargetArn;
            subscription.RedrivePolicy = deadLetterTargetArn is null ? null : value;
        }
        else if (name.Equals("DeliveryPolicy", StringComparison.OrdinalIgnoreCase))
        {
            SnsDeliveryPolicy.Validate(value);
            subscription.DeliveryPolicy = string.IsNullOrWhiteSpace(value) ? null : value;
        }
        else if (strict)
        {
            throw new InternalInvalidParameterException($"Unsupported attribute: {name}");
        }
    }

    private static string? NormalizeProtocol(string? protocol)
    {
        foreach (var known in new[] { "sqs", "http", "https" })
        {
            if (string.Equals(protocol, known, StringComparison.OrdinalIgnoreCase))
            {
                return known;
            }
        }
        return protocol;
    }

    private SnsTopicResource GetTopicByArn(string topicArn)
    {
        var topicName = GetTopicNameByArn(topicArn);
        return _bus.Topics.TryGetValue(topicName, out var topic)
            ? topic
            : throw new InternalNotFoundException($"Topic not found: {topicArn}");
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

    /// <summary>
    /// The topic's effective message size limit: its <c>MaximumMessageSize</c> attribute, or 256 KiB
    /// when the topic hasn't opted in to larger payloads.
    /// </summary>
    private static int GetMaxMessageSize(SnsTopicResource topic) =>
        topic.Attributes.TryGetValue(MaximumMessageSizeAttribute, out var value)
        && int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var size)
            ? size
            : DefaultMaxMessageSize;

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
                $"{errorPrefix}{MaximumMessageSizeAttribute}: {value} is not an integer between " +
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
        $"Invalid parameter: {MaximumMessageSizeAttribute} greater than {DefaultMaxMessageSize} bytes " +
        $"is not supported for the following protocol: [{protocol}]";

    /// <summary>
    /// The subscription side of the same constraint: a topic that accepts more than 256 KiB can't take
    /// an HTTP/S subscription, or a subscription beyond its hundredth.
    /// </summary>
    private void ValidateSubscriptionSupportsTopicMessageSize(string topicArn, string protocol)
    {
        var topicName = GetTopicNameByArn(topicArn);
        if (!_bus.Topics.TryGetValue(topicName, out var topic) || GetMaxMessageSize(topic) <= DefaultMaxMessageSize)
        {
            return;
        }

        if (protocol is "http" or "https")
        {
            throw new InternalInvalidParameterException(UnsupportedProtocolMessage(protocol));
        }

        if (_bus.Subscriptions.Values.Count(s => s.TopicArn == topicArn) >= LargeMessageSubscriptionLimit)
        {
            throw new InternalInvalidParameterException(
                $"Invalid parameter: A topic with {MaximumMessageSizeAttribute} greater than {DefaultMaxMessageSize} bytes " +
                $"supports a maximum of {LargeMessageSubscriptionLimit} subscriptions");
        }
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
