using System.Net;

namespace InMemoryAws;

/// <summary>Topic policies, enforced for callers.</summary>
internal sealed partial class InternalSnsClient
{
    /// <summary>
    /// Refuses the request with SNS's <c>AuthorizationError</c> when the topic's policy explicitly
    /// denies <paramref name="action"/> (named as IAM names it, e.g. <c>Publish</c> for PublishBatch too)
    /// to the caller.
    /// </summary>
    /// <remarks>
    /// As for queue and event bus policies (<see cref="ResourcePolicy"/>), only explicit Deny
    /// statements are enforced: the default policy, or one that only allows, serves every caller as
    /// before. The library does not see who sends an SNS request, so every request acts for the
    /// cloud's account (its root user). <paramref name="serviceKeys"/> are the action's own condition
    /// keys (<c>sns:Protocol</c> and <c>sns:Endpoint</c> for Subscribe); a statement conditioned on
    /// any other <c>sns:</c> key is skipped.
    /// </remarks>
    private void Authorize(SnsTopicResource topic, string action, IReadOnlyDictionary<string, string?>? serviceKeys = null)
    {
        string? document;
        lock (topic.SubscriptionGate)
        {
            document = topic.Attributes.GetValueOrDefault(SnsTopicAttributes.Policy);
        }

        if (ResourcePolicy.Parse(ResourcePolicyKind.Topic, document) is not { } policy)
        {
            return;
        }

        var callerArn = $"arn:aws:iam::{_cloud.CurrentAccountId}:root";
        var request = new ResourcePolicy.RequestContext(action, topic.Arn, _cloud.CurrentAccountId, callerArn, SourceIp: null)
        {
            ServiceKeys = serviceKeys
        };

        if (policy.DeniesExplicitly(request))
        {
            throw new AwsServiceException(
                $"User: {callerArn} is not authorized to perform: SNS:{action} on resource: {topic.Arn} " +
                "with an explicit deny in a resource-based policy")
            {
                ErrorCode = "AuthorizationError",
                StatusCode = HttpStatusCode.Forbidden
            };
        }
    }
}
