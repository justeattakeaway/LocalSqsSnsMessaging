using System.Text.Json;
using System.Text.Json.Nodes;

namespace LocalSqsSnsMessaging;

/// <summary>
/// Parses an SNS subscription redrive policy (<c>{"deadLetterTargetArn": "arn:aws:sqs:..."}</c>).
/// See https://docs.aws.amazon.com/sns/latest/dg/sns-dead-letter-queues.html
/// </summary>
internal static class SnsRedrivePolicy
{
    /// <summary>
    /// Validates the policy and returns the dead-letter queue ARN, or <see langword="null"/> when the
    /// policy is an empty JSON object (which removes any existing redrive policy). Throws
    /// <see cref="InternalInvalidParameterException"/> for a missing value, a malformed policy or a
    /// target that is not an SQS queue.
    /// </summary>
    public static string? ParseDeadLetterTargetArn(string? policy)
    {
        if (string.IsNullOrWhiteSpace(policy))
        {
            throw new InternalInvalidParameterException("Invalid parameter: RedrivePolicy: missing required value");
        }

        JsonObject? json;
        try
        {
            json = JsonNode.Parse(policy!) as JsonObject;
        }
        catch (JsonException)
        {
            json = null;
        }

        if (json is null)
        {
            throw new InternalInvalidParameterException("Invalid parameter: RedrivePolicy: Redrive policy must be a JSON object");
        }

        if (json.Count == 0)
        {
            return null;
        }

        if (json["deadLetterTargetArn"] is not JsonValue arnValue ||
            !arnValue.TryGetValue<string>(out var arn) ||
            string.IsNullOrWhiteSpace(arn))
        {
            throw new InternalInvalidParameterException("Invalid parameter: RedrivePolicy: deadLetterTargetArn is required");
        }

        if (!SnsArn.IsSqsQueueArn(arn))
        {
            throw new InternalInvalidParameterException("Invalid parameter: RedrivePolicy: deadLetterTargetArn must be a SQS queue");
        }

        return arn;
    }
}
