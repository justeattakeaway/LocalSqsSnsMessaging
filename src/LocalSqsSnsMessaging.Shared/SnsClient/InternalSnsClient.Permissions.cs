#pragma warning disable CS8600, CS8601, CS8602, CS8604 // Nullable reference warnings - internal POCOs use nullable properties but values are set at runtime

using System.Text.Json.Nodes;
using LocalSqsSnsMessaging.Sns.Model;

namespace LocalSqsSnsMessaging;

/// <summary>Topic permissions and tags.</summary>
internal sealed partial class InternalSnsClient
{
    /// <summary>SNS answers a tag request for a resource that doesn't exist with this message.</summary>
    private const string ResourceDoesNotExist = "Resource does not exist";

    /// <summary>The actions <c>AddPermission</c> can grant on a topic, as SNS names them.</summary>
    private static readonly HashSet<string> TopicPolicyActions = new(StringComparer.Ordinal)
    {
        "AddPermission", "DeleteTopic", "GetTopicAttributes", "ListSubscriptionsByTopic", "Publish",
        "Receive", "RemovePermission", "SetTopicAttributes", "Subscribe",
    };

    public Task<AddPermissionResponse> AddPermissionAsync(AddPermissionRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var topic = GetTopicByArn(request.TopicArn);
        ValidatePermission(request);

        lock (topic.SubscriptionGate)
        {
            var policy = topic.Attributes.TryGetValue(SnsTopicAttributes.Policy, out var policyJson)
                ? JsonNode.Parse(policyJson)!.AsObject()
                : new JsonObject
                {
                    ["Version"] = "2008-10-17",
                    ["Id"] = "__default_policy_ID",
                    ["Statement"] = new JsonArray()
                };

            if (policy["Statement"] is not JsonArray statements)
            {
                statements = [];
                policy["Statement"] = statements;
            }

            foreach (var stmt in statements)
            {
                if (stmt?["Sid"]?.GetValue<string>() == request.Label)
                {
                    throw new InternalInvalidParameterException("Invalid parameter: Statement already exists");
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
            topic.Attributes[SnsTopicAttributes.Policy] = policy.ToJsonString();
        }

        _bus.RecordOperation(AwsServiceName.Sns, SnsActionName.AddPermission, topic.Arn);
        return Task.FromResult(new AddPermissionResponse().SetCommonProperties());
    }

    /// <summary>
    /// Refuses an <c>AddPermission</c> grant SNS would not add to the policy: an account id that is
    /// not twelve digits, or an action that is not one of <see cref="TopicPolicyActions"/>.
    /// </summary>
    private static void ValidatePermission(AddPermissionRequest request)
    {
        if (request.AWSAccountId?.Exists(account => account is not { Length: 12 } || !account.All(c => c is >= '0' and <= '9')) == true)
        {
            throw new InternalInvalidParameterException("Invalid parameter: A provided account ID is not valid");
        }

        if (request.ActionName?.Exists(action => !TopicPolicyActions.Contains(action)) == true)
        {
            throw new InternalInvalidParameterException("Invalid parameter: Policy statement action out of service scope!");
        }
    }

    public Task<RemovePermissionResponse> RemovePermissionAsync(RemovePermissionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var topic = GetTopicByArn(request.TopicArn);

        lock (topic.SubscriptionGate)
        {
            // Removing a label the policy doesn't have succeeds and changes nothing, as on AWS.
            if (topic.Attributes.TryGetValue(SnsTopicAttributes.Policy, out var policyJson)
                && JsonNode.Parse(policyJson) is JsonObject policy
                && policy["Statement"] is JsonArray statements)
            {
                for (var i = 0; i < statements.Count; i++)
                {
                    if (statements[i]?["Sid"]?.GetValue<string>() == request.Label)
                    {
                        statements.RemoveAt(i);
                        topic.Attributes[SnsTopicAttributes.Policy] = policy.ToJsonString();
                        break;
                    }
                }
            }
        }

        _bus.RecordOperation(AwsServiceName.Sns, SnsActionName.RemovePermission, topic.Arn);
        return Task.FromResult(new RemovePermissionResponse().SetCommonProperties());
    }

    public Task<ListTagsForResourceResponse> ListTagsForResourceAsync(ListTagsForResourceRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var topic = GetTaggableTopic(request.ResourceArn);
        List<Tag> tags;
        lock (topic.SubscriptionGate)
        {
            tags = topic.Tags.Select(t => new Tag { Key = t.Key, Value = t.Value }).ToList();
        }

        _bus.RecordOperation(AwsServiceName.Sns, SnsActionName.ListTagsForResource, request.ResourceArn);
        return Task.FromResult(new ListTagsForResourceResponse
        {
            Tags = tags
        }.SetCommonProperties());
    }

    public Task<TagResourceResponse> TagResourceAsync(TagResourceRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var topic = GetTaggableTopic(request.ResourceArn);
        SnsTagValidation.ValidateTags(request.Tags, "tags");

        lock (topic.SubscriptionGate)
        {
            var newKeys = (request.Tags ?? []).Select(t => t.Key).Where(k => !topic.Tags.ContainsKey(k)).Distinct(StringComparer.Ordinal).Count();
            SnsTagValidation.EnsureWithinLimit(topic.Tags.Count + newKeys);
            foreach (var tag in request.Tags ?? [])
            {
                topic.Tags[tag.Key] = tag.Value ?? string.Empty;
            }
        }

        _bus.RecordOperation(AwsServiceName.Sns, SnsActionName.TagResource, request.ResourceArn);
        return Task.FromResult(new TagResourceResponse().SetCommonProperties());
    }

    public Task<UntagResourceResponse> UntagResourceAsync(UntagResourceRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var topic = GetTaggableTopic(request.ResourceArn);
        lock (topic.SubscriptionGate)
        {
            foreach (var tagKey in request.TagKeys ?? [])
            {
                topic.Tags.Remove(tagKey);
            }
        }

        _bus.RecordOperation(AwsServiceName.Sns, SnsActionName.UntagResource, request.ResourceArn);
        return Task.FromResult(new UntagResourceResponse().SetCommonProperties());
    }

    /// <summary>The topic a tag request names, or SNS's <c>ResourceNotFound</c>.</summary>
    private SnsTopicResource GetTaggableTopic(string? resourceArn)
    {
        var name = SnsArn.TopicName(resourceArn, "ResourceArn");
        return _bus.Topics.TryGetValue(name, out var topic)
            ? topic
            : throw new AwsServiceException(ResourceDoesNotExist) { ErrorCode = "ResourceNotFound", StatusCode = System.Net.HttpStatusCode.NotFound };
    }
}
