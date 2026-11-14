namespace LocalSqsSnsMessaging;

/// <summary>
/// Checks the ARNs SNS requests carry the way SNS does: a value that isn't an ARN at all is refused
/// with <c>InvalidParameter</c> before anything is looked up, so a malformed ARN never reads as a
/// missing resource.
/// </summary>
internal static class SnsArn
{
    /// <summary>
    /// Splits <paramref name="arn"/> into its colon-separated elements, refusing it as SNS does when it
    /// has fewer than the six an ARN must have.
    /// </summary>
    /// <param name="arn">The ARN as the caller sent it.</param>
    /// <param name="parameter">The request parameter it came from, as SNS names it in the error.</param>
    public static string[] Parse(string? arn, string parameter)
    {
        if (string.IsNullOrEmpty(arn))
        {
            throw new InternalInvalidParameterException($"Invalid parameter: {parameter}");
        }

        var parts = arn!.Split(':');
        if (parts.Length < 6)
        {
            throw new InternalInvalidParameterException(
                $"Invalid parameter: {parameter} Reason: An ARN must have at least 6 elements, not {parts.Length}");
        }

        if (!string.Equals(parts[0], "arn", StringComparison.Ordinal))
        {
            throw new InternalInvalidParameterException($"Invalid parameter: {parameter}");
        }

        return parts;
    }

    /// <summary>
    /// The topic name a topic ARN names, after <see cref="Parse"/> has accepted it. A subscription
    /// ARN's seventh element is its id, so the name is always the sixth.
    /// </summary>
    public static string TopicName(string? topicArn, string parameter = "TopicArn") => Parse(topicArn, parameter)[5];

    /// <summary>The ARN of a new subscription to <paramref name="topicArn"/>: the topic's ARN and a fresh id.</summary>
    public static string NewSubscriptionArn(string topicArn) => $"{topicArn}:{Guid.NewGuid():D}";

    /// <summary>
    /// Whether <paramref name="endpoint"/> is the ARN of an SQS queue, the only endpoint an
    /// <c>sqs</c> subscription can have. SNS doesn't check that the queue exists.
    /// </summary>
    public static bool IsSqsQueueArn(string? endpoint)
    {
        if (string.IsNullOrEmpty(endpoint))
        {
            return false;
        }

        var parts = endpoint!.Split(':');
        return parts.Length == 6
               && string.Equals(parts[0], "arn", StringComparison.Ordinal)
               && string.Equals(parts[2], "sqs", StringComparison.Ordinal)
               && parts[3].Length > 0
               && parts[4].Length > 0
               && parts[5].Length > 0;
    }
}
