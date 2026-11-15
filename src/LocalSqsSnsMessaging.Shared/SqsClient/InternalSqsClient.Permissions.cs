#pragma warning disable CS8600, CS8601, CS8602, CS8604, CS8619 // Nullable reference warnings - internal POCOs use nullable properties but values are set at runtime

using System.Text.Json.Nodes;
using LocalSqsSnsMessaging.Sqs.Model;

namespace LocalSqsSnsMessaging;

/// <summary>AddPermission and RemovePermission, which edit the queue's Policy.</summary>
internal sealed partial class InternalSqsClient
{
    public Task<AddPermissionResponse> AddPermissionAsync(AddPermissionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var queueName = GetQueueNameFromUrl(request.QueueUrl!);
        if (!Queues.TryGetValue(queueName, out var queue))
        {
            throw new InternalQueueDoesNotExistException();
        }
        Authorize(queue, SqsActionName.AddPermission);

        SqsQueueValidation.ValidatePermissionActions(request.Actions);

        var policy = ResourcePolicy.ParseOrEmpty(ResourcePolicyKind.Queue, queue.Attributes?.GetValueOrDefault(InternalQueueAttributeName.Policy), queue.Arn);
        if (policy.HasStatement(request.Label))
        {
            throw SqsRequestValidation.InvalidParameterValue("Label", request.Label, "Already exists.");
        }

        if (policy.StatementCount >= SqsQueueValidation.MaxPolicyStatements)
        {
            throw SqsRequestValidation.Error("OverLimit",
                $"{policy.StatementCount + 1} Statements were found, maximum allowed is {SqsQueueValidation.MaxPolicyStatements}. ");
        }

        var principals = new JsonArray();
        foreach (var accountId in request.AWSAccountIds ?? [])
        {
            principals.Add((JsonNode?)$"arn:aws:iam::{accountId}:root");
        }

        var actions = new JsonArray();
        foreach (var action in request.Actions ?? [])
        {
            actions.Add((JsonNode?)$"SQS:{action}");
        }

        // SQS writes a single principal or action as a plain string, and several as a list.
        var newStatement = new JsonObject
        {
            ["Sid"] = request.Label,
            ["Effect"] = "Allow",
            ["Principal"] = new JsonObject { ["AWS"] = SingleOrList(principals) },
            ["Action"] = SingleOrList(actions),
            ["Resource"] = queue.Arn
        };

        queue.Attributes ??= [];
        queue.Attributes[InternalQueueAttributeName.Policy] = policy.WithStatement(newStatement).ToJson();

        _bus.RecordOperation(AwsServiceName.Sqs, SqsActionName.AddPermission, queue.Arn);
        return Task.FromResult(new AddPermissionResponse().SetCommonProperties());

        static JsonNode SingleOrList(JsonArray values)
        {
            if (values.Count != 1)
            {
                return values;
            }

            var single = values[0]!;
            values.RemoveAt(0);
            return single;
        }
    }

    public Task<RemovePermissionResponse> RemovePermissionAsync(RemovePermissionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var queueName = GetQueueNameFromUrl(request.QueueUrl!);
        if (!Queues.TryGetValue(queueName, out var queue))
        {
            throw new InternalQueueDoesNotExistException();
        }
        Authorize(queue, SqsActionName.RemovePermission);

        if (ResourcePolicy.Parse(ResourcePolicyKind.Queue, queue.Attributes?.GetValueOrDefault(InternalQueueAttributeName.Policy)) is not { } policy
            || !policy.HasStatement(request.Label))
        {
            throw SqsRequestValidation.InvalidParameterValue("Label", request.Label, "can't find label on existing policy.");
        }

        // A policy left with no statements is removed.
        var remaining = policy.WithoutStatement(request.Label);
        queue.Attributes ??= [];
        if (remaining.StatementCount > 0)
        {
            queue.Attributes[InternalQueueAttributeName.Policy] = remaining.ToJson();
        }
        else
        {
            queue.Attributes.Remove(InternalQueueAttributeName.Policy);
        }

        _bus.RecordOperation(AwsServiceName.Sqs, SqsActionName.RemovePermission, queue.Arn);
        return Task.FromResult(new RemovePermissionResponse().SetCommonProperties());
    }

    /// <summary>
    /// Stores a just-written <c>Policy</c> as SQS does: an empty one removes the attribute, and a
    /// bare account-id principal reads back as the account's root ARN.
    /// </summary>
    private static void StorePolicy(Dictionary<string, string> attributes)
    {
        if (!attributes.TryGetValue(InternalQueueAttributeName.Policy, out var policy))
        {
            return;
        }

        if (string.IsNullOrEmpty(policy))
        {
            attributes.Remove(InternalQueueAttributeName.Policy);
        }
        else
        {
            attributes[InternalQueueAttributeName.Policy] = ResourcePolicy.Normalize(ResourcePolicyKind.Queue, policy);
        }
    }
}
