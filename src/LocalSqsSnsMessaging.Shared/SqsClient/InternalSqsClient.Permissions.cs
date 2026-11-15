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

        JsonObject policy;
        if (queue.Attributes?.TryGetValue("Policy", out var policyJson) == true)
        {
            policy = JsonNode.Parse(policyJson)!.AsObject();
        }
        else
        {
            policy = new JsonObject
            {
                ["Version"] = "2012-10-17",
                ["Id"] = $"{queue.Arn}/SQSDefaultPolicy",
                ["Statement"] = new JsonArray()
            };
        }

        var statements = policy["Statement"]!.AsArray();

        foreach (var stmt in statements)
        {
            if (stmt?["Sid"]?.GetValue<string>() == request.Label)
            {
                throw SqsRequestValidation.InvalidParameterValue("Label", request.Label, "Already exists.");
            }
        }

        if (statements.Count >= SqsQueueValidation.MaxPolicyStatements)
        {
            throw SqsRequestValidation.Error("OverLimit",
                $"{statements.Count + 1} Statements were found, maximum allowed is {SqsQueueValidation.MaxPolicyStatements}. ");
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

        statements.Add((JsonNode)newStatement);
        queue.Attributes ??= [];
        queue.Attributes["Policy"] = policy.ToJsonString();

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

        if (queue.Attributes?.TryGetValue("Policy", out var policyJson) != true)
        {
            throw SqsRequestValidation.InvalidParameterValue("Label", request.Label, "can't find label on existing policy.");
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
            throw SqsRequestValidation.InvalidParameterValue("Label", request.Label, "can't find label on existing policy.");
        }

        statements.RemoveAt(indexToRemove.Value);

        queue.Attributes ??= [];
        if (statements.Count > 0)
        {
            queue.Attributes["Policy"] = policy.ToJsonString();
        }
        else
        {
            queue.Attributes.Remove("Policy");
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
            attributes[InternalQueueAttributeName.Policy] = SqsQueuePolicy.Normalize(policy);
        }
    }
}
