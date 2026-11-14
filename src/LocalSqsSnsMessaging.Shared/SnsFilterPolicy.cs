using System.Text.Json;
using System.Text.Json.Nodes;
using LocalSqsSnsMessaging.Sns.Model;

namespace LocalSqsSnsMessaging;

/// <summary>
/// Validation and evaluation of SNS subscription filter policies.
/// See https://docs.aws.amazon.com/sns/latest/dg/sns-subscription-filter-policies.html
/// </summary>
internal static partial class SnsFilterPolicy
{
    public const string MessageAttributesScope = "MessageAttributes";
    public const string MessageBodyScope = "MessageBody";

    /// <summary>The most leaf keys one rule of a filter policy may have.</summary>
    private const int MaxKeys = 5;

    /// <summary>The most value combinations a filter policy may have.</summary>
    private const int MaxCombinations = 150;

    /// <summary>The deepest a <see cref="MessageBodyScope"/> filter policy may nest.</summary>
    private const int MaxNestingDepth = 5;

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
            return EventRulerJsonReader.Read(policy!, quoteSource: false) as JsonObject;
        }
        catch (EventRulerPatternException)
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
    /// with SNS's message when it is malformed. An empty policy, or <c>{}</c>, is valid and means "no filter".
    /// </summary>
    /// <remarks>
    /// SNS checks a policy in stages, and so does this: the text must be JSON; its compact form must
    /// follow the filter grammar (operators and their operands, reported with the position reached);
    /// a <see cref="MessageAttributesScope"/> policy must be flat; and once <c>$or</c> branches are
    /// expanded, no rule may have more than <see cref="MaxKeys"/> leaf keys or the policy more than
    /// <see cref="MaxCombinations"/> value combinations.
    /// </remarks>
    public static void Validate(string? policy, string scope)
    {
        if (string.IsNullOrWhiteSpace(policy))
        {
            return;
        }

        JsonNode? document;
        try
        {
            document = EventRulerJsonReader.Read(policy!, quoteSource: false);
        }
        catch (EventRulerPatternException ex)
        {
            throw Invalid($"failed to parse JSON. {ex.Message}");
        }

        if (document is JsonObject { Count: 0 })
        {
            return;
        }

        EventRulerRules compiled;
        try
        {
            compiled = EventRulerGrammar.Compile(WriteCompact(document));
        }
        catch (EventRulerPatternException ex)
        {
            throw Invalid(ex.Message);
        }

        if (compiled.IsNested && !string.Equals(scope, MessageBodyScope, StringComparison.Ordinal))
        {
            throw new InternalInvalidParameterException(
                $"Invalid parameter: Filter policy scope {MessageAttributesScope} does not support nested filter policy");
        }

        if (compiled.Rules.Any(rule => rule.Count > MaxKeys))
        {
            throw Invalid($"Filter policy can not have more than {MaxKeys} keys");
        }

        // Each rule contributes the product, over its leaf keys, of the key's value count times its
        // nesting depth; $or branches are separate rules, so their contributions add up.
        long combinations = 0;
        foreach (var rule in compiled.Rules)
        {
            long product = 1;
            foreach (var (values, depth) in rule.Values)
            {
                product = Math.Min(product * values * depth, MaxCombinations + 1);
            }
            combinations += product;
        }

        if (combinations > MaxCombinations)
        {
            throw Invalid("Filter policy is too complex");
        }

        if (compiled.Rules.Any(rule => rule.Values.Any(leaf => leaf.Depth > MaxNestingDepth)))
        {
            throw Invalid($"Filter policy can only have up to {MaxNestingDepth} levels of nesting");
        }
    }

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
