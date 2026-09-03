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
    /// policy is empty (which removes any existing redrive policy). Throws
    /// <see cref="InternalInvalidParameterException"/> for malformed policies or non-SQS targets.
    /// </summary>
    public static string? ParseDeadLetterTargetArn(string? policy)
    {
        if (string.IsNullOrWhiteSpace(policy))
        {
            return null;
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

        if (!arn.StartsWith("arn:aws:sqs:", StringComparison.Ordinal))
        {
            throw new InternalInvalidParameterException("Invalid parameter: RedrivePolicy: deadLetterTargetArn must be an SQS queue ARN");
        }

        return arn;
    }
}
