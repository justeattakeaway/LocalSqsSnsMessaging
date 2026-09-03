using System.Text.Json;
using System.Text.Json.Nodes;
using LocalSqsSnsMessaging.Sns.Model;

namespace LocalSqsSnsMessaging;

/// <summary>
/// Validation and evaluation of SNS subscription filter policies.
/// See https://docs.aws.amazon.com/sns/latest/dg/sns-subscription-filter-policies.html
/// </summary>
internal static class SnsFilterPolicy
{
    public const string MessageAttributesScope = "MessageAttributes";
    public const string MessageBodyScope = "MessageBody";

    /// <summary>
    /// Parses a filter policy string. Returns <see langword="null"/> for an empty policy (no
    /// filtering). Assumes the policy has already passed <see cref="Validate"/>; malformed JSON
    /// is treated as "no filter" rather than throwing, since it can only get here via a bug.
    /// </summary>
    public static JsonObject? Parse(string? policy)
    {
        if (string.IsNullOrWhiteSpace(policy))
        {
            return null;
        }

        try
        {
            return JsonNode.Parse(policy!) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Validates that <paramref name="scope"/> is a supported FilterPolicyScope value.</summary>
    public static bool IsValidScope(string? scope) =>
        string.Equals(scope, MessageAttributesScope, StringComparison.Ordinal) ||
        string.Equals(scope, MessageBodyScope, StringComparison.Ordinal);

    /// <summary>
    /// Validates a filter policy for the given scope, throwing <see cref="InternalInvalidParameterException"/>
    /// with an AWS-style message when it is malformed. An empty policy is valid and means "no filter".
    /// </summary>
    public static void Validate(string? policy, string scope)
    {
        if (string.IsNullOrWhiteSpace(policy))
        {
            return;
        }

        JsonNode? root;
        try
        {
            root = JsonNode.Parse(policy!);
        }
        catch (JsonException)
        {
            throw new InternalInvalidParameterException("Invalid parameter: FilterPolicy: Filter policy is not valid JSON");
        }

        if (root is not JsonObject obj)
        {
            throw new InternalInvalidParameterException("Invalid parameter: FilterPolicy: Filter policy must be a JSON object");
        }

        var allowNesting = string.Equals(scope, MessageBodyScope, StringComparison.Ordinal);
        ValidateObject(obj, allowNesting, depth: 1);
    }

    private static void ValidateObject(JsonObject obj, bool allowNesting, int depth)
    {
        foreach (var (key, value) in obj)
        {
            if (string.Equals(key, "$or", StringComparison.Ordinal))
            {
                if (value is not JsonArray orArray || orArray.Count == 0 || orArray.Any(x => x is not JsonObject))
                {
                    throw new InternalInvalidParameterException("Invalid parameter: FilterPolicy: \"$or\" must be an array of objects");
                }
                foreach (var branch in orArray)
                {
                    ValidateObject((JsonObject)branch!, allowNesting, depth);
                }
                continue;
            }

            switch (value)
            {
                case JsonArray candidates:
                    if (candidates.Count == 0)
                    {
                        throw new InternalInvalidParameterException($"Invalid parameter: FilterPolicy: Empty arrays are not allowed (\"{key}\")");
                    }
                    foreach (var candidate in candidates)
                    {
                        ValidateCandidate(key, candidate);
                    }
                    break;

                case JsonObject nested:
                    if (!allowNesting)
                    {
                        throw new InternalInvalidParameterException(
                            $"Invalid parameter: FilterPolicy: Filter policy scope {MessageAttributesScope} does not support nested filter policy (\"{key}\")");
                    }
                    if (depth >= 5)
                    {
                        throw new InternalInvalidParameterException("Invalid parameter: FilterPolicy: Filter policy can only have up to 5 levels of nesting");
                    }
                    ValidateObject(nested, allowNesting, depth + 1);
                    break;

                default:
                    throw new InternalInvalidParameterException($"Invalid parameter: FilterPolicy: \"{key}\" must have an array or object value");
            }
        }
    }

    private static void ValidateCandidate(string key, JsonNode? candidate)
    {
        if (candidate is JsonObject op)
        {
            if (op.Count != 1)
            {
                throw new InternalInvalidParameterException($"Invalid parameter: FilterPolicy: Only one operator is allowed per object (\"{key}\")");
            }
            var name = op.First().Key;
            if (name is not ("anything-but" or "prefix" or "suffix" or "equals-ignore-case" or "numeric" or "exists"))
            {
                throw new InternalInvalidParameterException($"Invalid parameter: FilterPolicy: Unrecognized match type {name} (\"{key}\")");
            }
            return;
        }

        if (candidate is JsonArray)
        {
            throw new InternalInvalidParameterException($"Invalid parameter: FilterPolicy: Nested arrays are not allowed (\"{key}\")");
        }
    }

    /// <summary>
    /// Returns true when the subscription should receive a message with the given body and attributes.
    /// A subscription without a filter policy always matches.
    /// </summary>
    public static bool Matches(SnsSubscription subscription, string message, Dictionary<string, MessageAttributeValue>? attributes)
    {
        var policy = subscription.ParsedFilterPolicy;
        if (policy is null)
        {
            return true;
        }

        if (string.Equals(subscription.FilterPolicyScope, MessageBodyScope, StringComparison.Ordinal))
        {
            // Body-scoped policies require the message to be a JSON object; anything else is dropped.
            JsonObject? body;
            try
            {
                body = JsonNode.Parse(message) as JsonObject;
            }
            catch (JsonException)
            {
                body = null;
            }

            return body is not null && JsonPatternMatcher.Matches(policy, body);
        }

        return JsonPatternMatcher.Matches(policy, AttributesToJson(attributes));
    }

    /// <summary>
    /// Projects SNS message attributes onto a JSON object so the shared matcher can evaluate them.
    /// <c>String</c> becomes a JSON string, <c>Number</c> a JSON number, <c>String.Array</c> is parsed
    /// as a JSON array, and <c>Binary</c> attributes are ignored (they can't be filtered on).
    /// </summary>
    private static JsonObject AttributesToJson(Dictionary<string, MessageAttributeValue>? attributes)
    {
        var result = new JsonObject();
        if (attributes is null)
        {
            return result;
        }

        foreach (var (name, attribute) in attributes)
        {
            var dataType = attribute.DataType ?? "String";
            var value = attribute.StringValue;
            if (value is null)
            {
                continue;
            }

            if (string.Equals(dataType, "String.Array", StringComparison.Ordinal))
            {
                try
                {
                    if (JsonNode.Parse(value) is JsonArray array)
                    {
                        result[name] = array;
                    }
                }
                catch (JsonException)
                {
                    // Not a valid array; the attribute can't match any candidate.
                }
            }
            else if (dataType.StartsWith("Number", StringComparison.Ordinal))
            {
                result[name] = double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)
                    ? JsonValue.Create(number)
                    : JsonValue.Create(value);
            }
            else if (dataType.StartsWith("String", StringComparison.Ordinal))
            {
                result[name] = JsonValue.Create(value);
            }
        }

        return result;
    }
}
