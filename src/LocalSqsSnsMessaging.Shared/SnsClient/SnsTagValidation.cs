using LocalSqsSnsMessaging.Sns.Model;

namespace LocalSqsSnsMessaging;

/// <summary>The limits SNS puts on topic tags, with its codes and messages for breaking them.</summary>
internal static class SnsTagValidation
{
    /// <summary>The most tags a topic can carry.</summary>
    public const int MaxTagsPerTopic = 50;

    private const int MaxKeyLength = 128;
    private const int MaxValueLength = 256;

    /// <summary>
    /// Refuses tags SNS wouldn't store: over-long keys or values, and keys in the reserved
    /// <c>aws:</c> namespace. <paramref name="member"/> names the request list in a validation error.
    /// </summary>
    public static void ValidateTags(IReadOnlyList<Tag>? tags, string member)
    {
        if (tags is null)
        {
            return;
        }

        for (var i = 0; i < tags.Count; i++)
        {
            var key = tags[i].Key ?? string.Empty;
            var value = tags[i].Value ?? string.Empty;
            if (key.Length > MaxKeyLength)
            {
                throw ValidationError($"1 validation error detected: Value '{key}' at '{member}.{i + 1}.member.key' failed " +
                                      $"to satisfy constraint: Member must have length less than or equal to {MaxKeyLength}");
            }

            if (value.Length > MaxValueLength)
            {
                throw ValidationError($"1 validation error detected: Value '{value}' at '{member}.{i + 1}.member.value' failed " +
                                      $"to satisfy constraint: Member must have length less than or equal to {MaxValueLength}");
            }

            if (key.StartsWith("aws:", StringComparison.OrdinalIgnoreCase))
            {
                throw new InternalInvalidParameterException(
                    "Invalid parameter: Tags Reason: Caller is an end user and not allowed to mutate system tags");
            }
        }
    }

    /// <summary>Refuses a change that would leave a topic with more than <see cref="MaxTagsPerTopic"/> tags.</summary>
    public static void EnsureWithinLimit(int tagCount)
    {
        if (tagCount > MaxTagsPerTopic)
        {
            throw new AwsServiceException("Could not complete request: tag quota of per resource exceeded")
            {
                ErrorCode = "TagLimitExceeded",
            };
        }
    }

    private static AwsServiceException ValidationError(string message) => new(message) { ErrorCode = "ValidationError" };
}
