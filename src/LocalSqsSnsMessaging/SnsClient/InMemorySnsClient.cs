using System.Runtime.CompilerServices;
using System.Text;
using Amazon.Auth.AccessControlPolicy;
using Amazon.Auth.AccessControlPolicy.ActionIdentifiers;
using Amazon.Runtime;
using Amazon.Runtime.SharedInterfaces;
using Amazon.SimpleNotificationService;
using Amazon.SimpleNotificationService.Model;
using RemovePermissionRequest = Amazon.SimpleNotificationService.Model.RemovePermissionRequest;
using RemovePermissionResponse = Amazon.SimpleNotificationService.Model.RemovePermissionResponse;

namespace LocalSqsSnsMessaging;

/// <summary>
/// Represents an in-memory implementation of Amazon Simple Notification Service (SNS) client.
/// This class provides methods to interact with SNS topics and subscriptions in a local, in-memory environment,
/// primarily for testing and development purposes without connecting to actual AWS services.
/// </summary>
public sealed partial class InMemorySnsClient : IAmazonSimpleNotificationService
{
    private readonly InMemoryAwsBus _bus;
    private readonly Lazy<ISimpleNotificationServicePaginatorFactory> _paginators;
    
    private const int MaxMessageSize = 262144;

    internal InMemorySnsClient(InMemoryAwsBus bus)
    {
        _bus = bus;
        _paginators = new(() => GetPaginatorFactory(this));
    }

#pragma warning disable CA1063
    void IDisposable.Dispose()
#pragma warning restore CA1063
    {
    }

    IClientConfig? IAmazonService.Config => null;

    public async Task<string> SubscribeQueueAsync(string topicArn, ICoreAmazonSQS sqsClient, string sqsQueueUrl)
    {
        ArgumentNullException.ThrowIfNull(sqsClient);
        
        // Get the queue's existing policy
        var queueAttributes = await sqsClient.GetAttributesAsync(sqsQueueUrl).ConfigureAwait(true);
        
        var sqsQueueArn = queueAttributes["QueueArn"];

        string? policyStr = null;
        if(queueAttributes.TryGetValue("Policy", out var attribute))
        {
            policyStr = attribute;
        }
        var policy = string.IsNullOrEmpty(policyStr) ? new Policy() : Policy.FromJson(policyStr);

        if (!HasSqsPermission(policy, topicArn, sqsQueueArn))
        {
            AddSqsPermission(policy, topicArn, sqsQueueArn);
        }

        var response = await SubscribeAsync(new SubscribeRequest
        {
            TopicArn = topicArn,
            Protocol = "sqs",
            Endpoint = sqsQueueArn,
        }).ConfigureAwait(true);

        var setAttributes = new Dictionary<string, string> { { "Policy", policy.ToJson() } };
        await sqsClient.SetAttributesAsync(sqsQueueUrl, setAttributes).ConfigureAwait(true);

        return response.SubscriptionArn;
    }

    public async Task<IDictionary<string, string>> SubscribeQueueToTopicsAsync(IList<string> topicArns,
        ICoreAmazonSQS sqsClient, string sqsQueueUrl)
    {
        ArgumentNullException.ThrowIfNull(topicArns);
        
        Dictionary<string, string> topicSubscriptionMapping = new();
        foreach (var topicArn in topicArns)
        {
            var subscriptionArn = await SubscribeQueueAsync(topicArn, sqsClient, sqsQueueUrl).ConfigureAwait(true);
            topicSubscriptionMapping.Add(topicArn, subscriptionArn);
        }

        return topicSubscriptionMapping;
    }

    public Task<Topic?> FindTopicAsync(string topicName)
    {
        if (!_bus.Topics.TryGetValue(topicName, out var topic))
        {
            return Task.FromResult<Topic?>(null);
        }

        return Task.FromResult<Topic?>(new Topic
        {
            TopicArn = topic.Arn
        });
    }

    public Task<CreateTopicResponse> CreateTopicAsync(string name, CancellationToken cancellationToken = default)
    {
        return CreateTopicAsync(new CreateTopicRequest { Name = name }, cancellationToken);
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

        return Task.FromResult(new CreateTopicResponse
        {
            TopicArn = topicArn
        }.SetCommonProperties());
    }

    public Task<DeleteTopicResponse> DeleteTopicAsync(string topicArn, CancellationToken cancellationToken = default)
    {
        return DeleteTopicAsync(new DeleteTopicRequest { TopicArn = topicArn }, cancellationToken);
    }

    public Task<DeleteTopicResponse> DeleteTopicAsync(DeleteTopicRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        
        var topicName = GetTopicNameByArn(request.TopicArn);
        _bus.Topics.TryRemove(topicName, out _);

        return Task.FromResult(new DeleteTopicResponse().SetCommonProperties());
    }

    public Task<GetSubscriptionAttributesResponse> GetSubscriptionAttributesAsync(string subscriptionArn,
        CancellationToken cancellationToken = default)
    {
        return GetSubscriptionAttributesAsync(new GetSubscriptionAttributesRequest { SubscriptionArn = subscriptionArn },
            cancellationToken);
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

    public Task<GetTopicAttributesResponse> GetTopicAttributesAsync(string topicArn,
        CancellationToken cancellationToken = default)
    {
        return GetTopicAttributesAsync(new GetTopicAttributesRequest { TopicArn = topicArn }, cancellationToken);
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

        return Task.FromResult(new GetTopicAttributesResponse
        {
            Attributes = topic.Attributes
        }.SetCommonProperties());
    }

    public Task<ListSubscriptionsResponse> ListSubscriptionsAsync(CancellationToken cancellationToken = default)
    {
        var subscriptions = _bus.Subscriptions.Values.ToList();
        return Task.FromResult(new ListSubscriptionsResponse
        {
            Subscriptions = subscriptions.Select(s => new Subscription
            {
                SubscriptionArn = s.SubscriptionArn,
                TopicArn = s.TopicArn,
                Protocol = s.Protocol,
                Endpoint = s.EndPoint,
                Owner = _bus.CurrentAccountId,
            }).ToList()
        }.SetCommonProperties());
    }

    public Task<ListSubscriptionsResponse> ListSubscriptionsAsync(string nextToken,
        CancellationToken cancellationToken = default)
    {
        return ListSubscriptionsAsync(new ListSubscriptionsRequest { NextToken = nextToken }, cancellationToken);
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

    public Task<ListSubscriptionsByTopicResponse> ListSubscriptionsByTopicAsync(string topicArn, string nextToken,
        CancellationToken cancellationToken = default)
    {
        return ListSubscriptionsByTopicAsync(new ListSubscriptionsByTopicRequest
        {
            TopicArn = topicArn,
            NextToken = nextToken
        }, cancellationToken);
    }

    public Task<ListSubscriptionsByTopicResponse> ListSubscriptionsByTopicAsync(string topicArn,
        CancellationToken cancellationToken = default)
    {
        return ListSubscriptionsByTopicAsync(new ListSubscriptionsByTopicRequest { TopicArn = topicArn },
            cancellationToken);
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
        
        return Task.FromResult(new ListTagsForResourceResponse
        {
            Tags = tags
        }.SetCommonProperties());
    }

    public Task<ListTopicsResponse> ListTopicsAsync(CancellationToken cancellationToken = default)
    {
        return Task.FromResult(new ListTopicsResponse
        {
            Topics = _bus.Topics.Values.Select(t => new Topic
            {
                TopicArn = t.Arn
            }).ToList()
        }.SetCommonProperties());
    }

    public Task<ListTopicsResponse> ListTopicsAsync(string nextToken, CancellationToken cancellationToken = default)
    {
        return ListTopicsAsync(new ListTopicsRequest { NextToken = nextToken }, cancellationToken);
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
    
    public Task<PublishResponse> PublishAsync(string topicArn, string message,
        CancellationToken cancellationToken = default)
    {
        return PublishAsync(topicArn, message, null, cancellationToken);
    }

    public Task<PublishResponse> PublishAsync(string topicArn, string message, string? subject,
        CancellationToken cancellationToken = default)
    {
        return PublishAsync(new PublishRequest
        {
            TopicArn = topicArn,
            Message = message,
            Subject = subject
        }, cancellationToken);
    }

    public Task<PublishResponse> PublishAsync(PublishRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var messageSize = CalculateMessageSize(request.Message, request.MessageAttributes);
        if (messageSize > MaxMessageSize)
        {
            throw new InvalidParameterException($"Message size has exceeded the limit of {MaxMessageSize} bytes.");
        }
        
        var topic = GetTopicByArn(request.TopicArn);
        var result = topic.PublishAction.Execute(request);

        return Task.FromResult(result);
    }
    
    private static int CalculateMessageSize(string message, Dictionary<string, MessageAttributeValue>? messageAttributes)
    {
        var totalSize = 0;

        // Add message body size
        totalSize += Encoding.UTF8.GetByteCount(message);

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


    public Task<PublishBatchResponse> PublishBatchAsync(PublishBatchRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var totalSize = request.PublishBatchRequestEntries
            .Sum(requestEntry => CalculateMessageSize(requestEntry.Message, requestEntry.MessageAttributes));
        if (totalSize > MaxMessageSize)
        {
            throw new InvalidParameterException($"Message size has exceeded the limit of {MaxMessageSize} bytes.");
        }
        
        var topic = GetTopicByArn(request.TopicArn);
        var result = topic.PublishAction.ExecuteBatch(request);

        return Task.FromResult(result);
    }

    public Task<RemovePermissionResponse> RemovePermissionAsync(string topicArn, string label,
        CancellationToken cancellationToken = default)
    {
        return RemovePermissionAsync(new RemovePermissionRequest(), cancellationToken);
    }

    public Task<RemovePermissionResponse> RemovePermissionAsync(RemovePermissionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        
        var topic = GetTopicByArn(request.TopicArn);
        
        var policy = topic.Attributes.TryGetValue("Policy", out var policyJson)
            ? Policy.FromJson(policyJson)
            : new Policy($"{topic.Arn}/SNSDefaultPolicy");

        var statementToRemove = policy.Statements.FirstOrDefault(s => s.Id == request.Label);
        if (statementToRemove == null)
        {
            throw new ArgumentException($"Value {request.Label} for parameter Label is invalid. Reason: can't find label.");
        }

        policy.Statements.Remove(statementToRemove);

        if (policy.Statements.Any())
        {
            topic.Attributes["Policy"] = policy.ToJson();
        }
        else
        {
            topic.Attributes.Remove("Policy");
        }

        return Task.FromResult(new RemovePermissionResponse().SetCommonProperties());
    }
    
    public Task<SetSubscriptionAttributesResponse> SetSubscriptionAttributesAsync(string subscriptionArn,
        string attributeName, string attributeValue,
        CancellationToken cancellationToken = default)
    {
        return SetSubscriptionAttributesAsync(
            new SetSubscriptionAttributesRequest
            {
                SubscriptionArn = subscriptionArn,
                AttributeName = attributeName,
                AttributeValue = attributeValue
            },
            cancellationToken);
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

        return Task.FromResult(new SetSubscriptionAttributesResponse().SetCommonProperties());
    }

    public Task<SetTopicAttributesResponse> SetTopicAttributesAsync(string topicArn, string attributeName,
        string attributeValue,
        CancellationToken cancellationToken = default)
    {
        return SetTopicAttributesAsync(new SetTopicAttributesRequest
        {
            TopicArn = topicArn,
            AttributeName = attributeName,
            AttributeValue = attributeValue
        }, cancellationToken);
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
        return Task.FromResult(new SetTopicAttributesResponse().SetCommonProperties());
    }

    public Task<SubscribeResponse> SubscribeAsync(string topicArn, string protocol, string endpoint,
        CancellationToken cancellationToken = default)
    {
        return SubscribeAsync(new SubscribeRequest
        {
            TopicArn = topicArn,
            Protocol = protocol,
            Endpoint = endpoint
        }, cancellationToken);
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
            request.Attributes.TryGetValue("RawMessageDelivery", out var rawMessageDelivery) &&
            bool.TryParse(rawMessageDelivery, out var isRawMessageDelivery) &&
            isRawMessageDelivery;
        
        var parsedFilterPolicy =
            request.Attributes.TryGetValue("FilterPolicy", out var filterPolicy) ? filterPolicy : string.Empty;
        
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
        
        return Task.FromResult(new TagResourceResponse().SetCommonProperties());
    }

    public Task<UnsubscribeResponse> UnsubscribeAsync(string subscriptionArn,
        CancellationToken cancellationToken = default)
    {
        return UnsubscribeAsync(new UnsubscribeRequest { SubscriptionArn = subscriptionArn }, cancellationToken);
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
        
        return Task.FromResult(new UntagResourceResponse().SetCommonProperties());
    }

    private static void AddSqsPermission(Policy policy, string topicArn, string sqsQueueArn)
    {
        var statement = new Statement(Statement.StatementEffect.Allow);
#pragma warning disable CS0612,CS0618
        statement.Actions.Add(SQSActionIdentifiers.SendMessage);
#pragma warning restore CS0612,CS0618
        statement.Resources.Add(new Resource(sqsQueueArn));
        statement.Conditions.Add(ConditionFactory.NewSourceArnCondition(topicArn));
        statement.Principals.Add(new Principal("*"));
        policy.Statements.Add(statement);
    }
    
    private static bool HasSqsPermission(Policy policy, string topicArn, string sqsQueueArn)
    {
        foreach (var statement in policy.Statements)
        {
            // See if the statement contains the topic as a resource
            var containsResource = statement.Resources.Any(resource => resource.Id.Equals(sqsQueueArn, StringComparison.OrdinalIgnoreCase));

            // If queue found as the resource see if the condition is for this topic
            if (containsResource)
            {
                foreach (var condition in statement.Conditions)
                {
                    if ((string.Equals(condition.Type, ConditionFactory.StringComparisonType.StringLike.ToString(), StringComparison.OrdinalIgnoreCase) ||
                         string.Equals(condition.Type, ConditionFactory.StringComparisonType.StringEquals.ToString(), StringComparison.OrdinalIgnoreCase) ||
                         string.Equals(condition.Type, ConditionFactory.ArnComparisonType.ArnEquals.ToString(), StringComparison.OrdinalIgnoreCase) ||
                         string.Equals(condition.Type, ConditionFactory.ArnComparisonType.ArnLike.ToString(), StringComparison.OrdinalIgnoreCase)) &&
                        string.Equals(condition.ConditionKey, ConditionFactory.SOURCE_ARN_CONDITION_KEY, StringComparison.OrdinalIgnoreCase) &&
                        condition.Values.Contains<string>(topicArn))
                        return true;
                }
            }
        }

        return false;
    }
    
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

    public Task<AddPermissionResponse> AddPermissionAsync(string topicArn, string label,
        List<string> awsAccountId, List<string> actionName, CancellationToken cancellationToken = default)
    {
        return AddPermissionAsync(new AddPermissionRequest
        {
            TopicArn = topicArn,
            Label = label,
            AWSAccountId = awsAccountId,
            ActionName = actionName
        }, cancellationToken);
    }
    
    public Task<AddPermissionResponse> AddPermissionAsync(AddPermissionRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        
        var topic = GetTopicByArn(request.TopicArn);
        
        var policy = topic.Attributes.TryGetValue("Policy", out var policyJson)
            ? Policy.FromJson(policyJson)
            : new Policy($"{topic.Arn}/SNSDefaultPolicy");

        var statement = new Statement(Statement.StatementEffect.Allow)
        {
            Id = request.Label,
            Actions = request.ActionName.Select(action => new ActionIdentifier($"SNS:{action}")).ToList()
        };

        statement.Resources.Add(new Resource(topic.Arn));
        
        foreach (var accountId in request.AWSAccountId)
        {
            statement.Principals.Add(new Principal($"arn:aws:iam::{accountId}:root"));
        }

        if (policy.CheckIfStatementExists(statement))
        {
            throw new ArgumentException($"Value {request.Label} for parameter Label is invalid. Reason: Already exists.");
        }

        policy.Statements.Add(statement);
        topic.Attributes["Policy"] = policy.ToJson();

        return Task.FromResult(new AddPermissionResponse().SetCommonProperties());
    }

    [UnsafeAccessor(UnsafeAccessorKind.Constructor)]
    private static extern SimpleNotificationServicePaginatorFactory GetPaginatorFactory(IAmazonSimpleNotificationService client); 
    
    public ISimpleNotificationServicePaginatorFactory Paginators => _paginators.Value;
}