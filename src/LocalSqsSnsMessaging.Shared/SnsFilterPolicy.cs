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
        switch (candidate)
        {
            case JsonObject op:
                if (op.Count != 1)
                {
                    throw Invalid($"Only one operator is allowed per object (\"{key}\")");
                }
                ValidateOperator(key, op.First().Key, op.First().Value);
                break;

            case JsonArray:
                throw Invalid($"Nested arrays are not allowed (\"{key}\")");
        }
    }

    // Each operator's operand has a fixed shape; a policy that parses but has a malformed operand
    // would otherwise be stored and then silently match more (or less) than intended.
    private static void ValidateOperator(string key, string name, JsonNode? operand)
    {
        switch (name)
        {
            case "exists":
                if (!IsBool(operand))
                {
                    throw Invalid($"\"exists\" must be true or false (\"{key}\")");
                }
                break;

            case "prefix":
            case "suffix":
                var ignoreCase = operand is JsonObject affix && affix.Count == 1 &&
                                 affix.TryGetPropertyValue("equals-ignore-case", out var affixValue) && IsString(affixValue);
                if (!IsString(operand) && !ignoreCase)
                {
                    throw Invalid($"\"{name}\" must be a string (\"{key}\")");
                }
                break;

            case "equals-ignore-case":
            case "wildcard":
            case "cidr":
                if (!IsString(operand))
                {
                    throw Invalid($"\"{name}\" must be a string (\"{key}\")");
                }
                break;

            case "numeric":
                ValidateNumeric(key, operand);
                break;

            case "anything-but":
                ValidateAnythingBut(key, operand);
                break;

            default:
                throw Invalid($"Unrecognized match type {name} (\"{key}\")");
        }
    }

    private static void ValidateNumeric(string key, JsonNode? operand)
    {
        // Either a single comparison ["<op>", n] or a range ["> or >=", low, "< or <=", high].
        if (operand is not JsonArray spec || (spec.Count != 2 && spec.Count != 4))
        {
            throw Invalid($"\"numeric\" must be an operator followed by a number, or a range of two (\"{key}\")");
        }

        for (var i = 0; i < spec.Count; i += 2)
        {
            if (!IsString(spec[i]) || spec[i]!.GetValue<string>() is not ("=" or "!=" or "<" or "<=" or ">" or ">=") || !IsNumber(spec[i + 1]))
            {
                throw Invalid($"\"numeric\" must be an operator followed by a number (\"{key}\")");
            }
        }

        if (spec.Count == 4 &&
            (spec[0]!.GetValue<string>() is not (">" or ">=") || spec[2]!.GetValue<string>() is not ("<" or "<=")))
        {
            throw Invalid($"\"numeric\" range must be a lower bound followed by an upper bound (\"{key}\")");
        }
    }

    private static void ValidateAnythingBut(string key, JsonNode? operand)
    {
        switch (operand)
        {
            case JsonValue when IsString(operand) || IsNumber(operand):
                return;

            case JsonArray values when values.Count > 0 && values.All(v => IsString(v) || IsNumber(v)):
                return;

            case JsonObject inner when inner.Count == 1 &&
                                       inner.First().Key is "prefix" or "suffix" or "equals-ignore-case" or "wildcard" &&
                                       (IsString(inner.First().Value) ||
                                        inner.First().Value is JsonArray strings && strings.Count > 0 && strings.All(IsString)):
                return;

            default:
                throw Invalid($"\"anything-but\" must be a value, a list of values, or a prefix/suffix/equals-ignore-case/wildcard operator (\"{key}\")");
        }
    }

    private static bool IsString(JsonNode? node) => node is JsonValue v && v.GetValueKind() == JsonValueKind.String;
    private static bool IsNumber(JsonNode? node) => node is JsonValue v && v.GetValueKind() == JsonValueKind.Number;
    private static bool IsBool(JsonNode? node) => node is JsonValue v && v.GetValueKind() is JsonValueKind.True or JsonValueKind.False;

    private static InternalInvalidParameterException Invalid(string message) =>
        new($"Invalid parameter: FilterPolicy: {message}");

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
