using System.Text.Json;
using System.Text.Json.Nodes;

namespace LocalSqsSnsMessaging;

/// <summary>
/// The topic attributes SNS knows, their defaults, and the values each accepts, with SNS's codes and
/// messages for what it refuses.
/// </summary>
internal static class SnsTopicAttributes
{
    public const string DisplayName = "DisplayName";
    public const string Policy = "Policy";
    public const string DeliveryPolicy = "DeliveryPolicy";
    public const string EffectiveDeliveryPolicy = "EffectiveDeliveryPolicy";
    public const string KmsMasterKeyId = "KmsMasterKeyId";
    public const string SignatureVersion = "SignatureVersion";
    public const string TracingConfig = "TracingConfig";
    public const string FifoTopic = "FifoTopic";
    public const string ContentBasedDeduplication = "ContentBasedDeduplication";
    public const string FifoThroughputScope = "FifoThroughputScope";
    public const string ArchivePolicy = "ArchivePolicy";
    public const string BeginningArchiveTime = "BeginningArchiveTime";
    public const string MaximumMessageSize = "MaximumMessageSize";

    /// <summary>The largest <c>DisplayName</c> SNS accepts, in characters.</summary>
    private const int MaxDisplayNameLength = 100;

    /// <summary>The longest an <c>ArchivePolicy</c> can keep messages, in days.</summary>
    private const int MaxArchiveRetentionDays = 365;

    /// <summary>The longest topic name SNS accepts, <c>.fifo</c> suffix included.</summary>
    private const int MaxTopicNameLength = 256;

    /// <summary>The delivery-status logging attributes: a role ARN, or a sample rate, per protocol.</summary>
    private static readonly HashSet<string> FeedbackRoleAttributes = new(
        from protocol in new[] { "Application", "HTTP", "Lambda", "SQS", "Firehose" }
        from outcome in new[] { "Success", "Failure" }
        select $"{protocol}{outcome}FeedbackRoleArn",
        StringComparer.Ordinal);

    private static readonly HashSet<string> FeedbackSampleRateAttributes = new(
        from protocol in new[] { "Application", "HTTP", "Lambda", "SQS", "Firehose" }
        select $"{protocol}SuccessFeedbackSampleRate",
        StringComparer.Ordinal);

    /// <summary>Attributes any topic can set.</summary>
    private static readonly HashSet<string> SettableOnEveryTopic = new(StringComparer.Ordinal)
    {
        DisplayName, Policy, DeliveryPolicy, KmsMasterKeyId, SignatureVersion, TracingConfig, MaximumMessageSize,
        "DataProtectionPolicy",
    };

    /// <summary>Attributes only a FIFO topic can set.</summary>
    private static readonly HashSet<string> SettableOnFifoTopics = new(StringComparer.Ordinal)
    {
        ContentBasedDeduplication, FifoThroughputScope, ArchivePolicy,
    };

    /// <summary>
    /// Whether <paramref name="name"/> is an attribute <c>SetTopicAttributes</c> accepts on a topic
    /// that is (or isn't) FIFO. <c>FifoTopic</c> is fixed when the topic is created.
    /// </summary>
    public static bool IsSettable(string name, bool fifoTopic) =>
        SettableOnEveryTopic.Contains(name)
        || FeedbackRoleAttributes.Contains(name)
        || FeedbackSampleRateAttributes.Contains(name)
        || (fifoTopic && SettableOnFifoTopics.Contains(name));

    /// <summary>Whether <c>CreateTopic</c> accepts <paramref name="name"/> in its attribute map.</summary>
    public static bool IsCreatable(string name) =>
        string.Equals(name, FifoTopic, StringComparison.Ordinal) || IsSettable(name, fifoTopic: true);

    /// <summary>Whether <paramref name="name"/> is a valid topic name for a FIFO or a standard topic.</summary>
    public static bool IsValidTopicName(string? name, bool fifo)
    {
        if (string.IsNullOrEmpty(name) || name!.Length > MaxTopicNameLength)
        {
            return false;
        }

        var hasSuffix = name.EndsWith(".fifo", StringComparison.Ordinal);
        if (hasSuffix != fifo)
        {
            return false;
        }

        var stem = hasSuffix ? name[..^".fifo".Length] : name;
        return stem.Length > 0 && stem.All(c => c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '-' or '_');
    }

    /// <summary>The access policy SNS gives a new topic: the owner's account may do anything with it.</summary>
    public static string DefaultPolicy(string topicArn, string accountId) => new JsonObject
    {
        ["Version"] = "2008-10-17",
        ["Id"] = "__default_policy_ID",
        ["Statement"] = new JsonArray(new JsonObject
        {
            ["Sid"] = "__default_statement_ID",
            ["Effect"] = "Allow",
            ["Principal"] = new JsonObject { ["AWS"] = "*" },
            ["Action"] = new JsonArray(
                "SNS:GetTopicAttributes",
                "SNS:SetTopicAttributes",
                "SNS:AddPermission",
                "SNS:RemovePermission",
                "SNS:DeleteTopic",
                "SNS:Subscribe",
                "SNS:ListSubscriptionsByTopic",
                "SNS:Publish"),
            ["Resource"] = topicArn,
            ["Condition"] = new JsonObject
            {
                ["StringEquals"] = new JsonObject { ["AWS:SourceOwner"] = accountId },
            },
        }),
    }.ToJsonString();

    /// <summary>
    /// <c>EffectiveDeliveryPolicy</c>: the topic's <c>DeliveryPolicy</c> merged over SNS's default HTTP
    /// delivery policy, so every section the topic doesn't set still reads its default.
    /// </summary>
    public static string EffectiveDeliveryPolicyFor(string? deliveryPolicy)
    {
        var effective = new JsonObject
        {
            ["http"] = new JsonObject
            {
                ["defaultHealthyRetryPolicy"] = new JsonObject
                {
                    ["minDelayTarget"] = 20,
                    ["maxDelayTarget"] = 20,
                    ["numRetries"] = 3,
                    ["numMaxDelayRetries"] = 0,
                    ["numNoDelayRetries"] = 0,
                    ["numMinDelayRetries"] = 0,
                    ["backoffFunction"] = "linear",
                },
                ["disableSubscriptionOverrides"] = false,
                ["defaultRequestPolicy"] = new JsonObject
                {
                    ["headerContentType"] = SnsDeliveryPolicy.DefaultContentType,
                },
            },
        };

        if (!string.IsNullOrWhiteSpace(deliveryPolicy) && TryParseObject(deliveryPolicy!) is { } overrides)
        {
            Merge(effective, overrides);
        }

        return effective.ToJsonString();
    }

    private static void Merge(JsonObject target, JsonObject source)
    {
        foreach (var (key, value) in source)
        {
            if (value is JsonObject sourceObject && target[key] is JsonObject targetObject)
            {
                Merge(targetObject, sourceObject);
            }
            else
            {
                target[key] = value?.DeepClone();
            }
        }
    }

    private static JsonObject? TryParseObject(string json)
    {
        try
        {
            return JsonNode.Parse(json) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Refuses a value SNS wouldn't store for <paramref name="name"/>. <paramref name="current"/> is the
    /// topic's present value, for attributes whose changes are limited.
    /// </summary>
    public static void Validate(string name, string? value, string? current)
    {
        value ??= string.Empty;
        switch (name)
        {
            case DisplayName:
                if (value.Length > MaxDisplayNameLength)
                {
                    throw new InternalInvalidParameterException("Invalid parameter: DisplayName");
                }

                break;

            case Policy:
                if (TryParseObject(value) is null)
                {
                    throw new InternalInvalidParameterException("Invalid parameter: Policy Error: null");
                }

                break;

            case DeliveryPolicy:
                SnsDeliveryPolicy.ValidateTopicPolicy(value);
                break;

            case SignatureVersion:
                if (value is not ("1" or "2"))
                {
                    throw new InternalInvalidParameterException(
                        $"Invalid parameter: SignatureVersion: Invalid value '[{value}]'. Must be 1 or 2.");
                }

                break;

            case TracingConfig:
                if (value is not ("PassThrough" or "Active"))
                {
                    throw new InternalInvalidParameterException($"Invalid parameter: Invalid tracing config value: {value}");
                }

                break;

            case ContentBasedDeduplication:
                if (value is not ("true" or "false"))
                {
                    throw new InternalInvalidParameterException("Invalid parameter: ContentBasedDeduplication");
                }

                break;

            case FifoThroughputScope:
                if (value is not ("Topic" or "MessageGroup"))
                {
                    throw new InternalInvalidParameterException($"Invalid parameter: Invalid FifoThroughputScope: {value}");
                }

                if (current == "MessageGroup" && value == "Topic")
                {
                    throw new InternalInvalidParameterException(
                        "Invalid parameter: Modifying FifoThroughputScope from MessageGroup to Topic is not supported");
                }

                break;

            case ArchivePolicy:
                if (!TryParseArchivePolicy(value, out _))
                {
                    throw new InternalInvalidParameterException(
                        $"Invalid parameter: ArchivePolicy: MessageRetentionPeriod must be an integer between 1 and {MaxArchiveRetentionDays}");
                }

                break;

            default:
                if (FeedbackSampleRateAttributes.Contains(name)
                    && (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var rate) || rate > 100))
                {
                    throw new InternalInvalidParameterException(
                        $"Invalid parameter: {name}: {value} value provided is not an integer between 0-100");
                }

                break;
        }
    }

    /// <summary>
    /// Whether setting <paramref name="name"/> to <paramref name="value"/> removes the attribute, as an
    /// empty <c>KmsMasterKeyId</c> or <c>DeliveryPolicy</c> does. An empty <c>DisplayName</c> is stored
    /// as the empty string, its default.
    /// </summary>
    public static bool ClearsAttribute(string name, string? value) =>
        string.IsNullOrEmpty(value) && name is not (DisplayName or Policy);

    /// <summary>
    /// How long an <c>ArchivePolicy</c> keeps messages, or null for a policy that turns archiving off
    /// (an empty value or <c>{}</c>) or that isn't valid.
    /// </summary>
    public static TimeSpan? ArchiveRetention(string? policy) =>
        TryParseArchivePolicy(policy, out var retention) ? retention : null;

    /// <summary>
    /// Parses an <c>ArchivePolicy</c> (<c>{"MessageRetentionPeriod": "&lt;days&gt;"}</c>): false when it
    /// isn't valid; otherwise <paramref name="retention"/> is the retention period, or null when the
    /// policy turns archiving off.
    /// </summary>
    private static bool TryParseArchivePolicy(string? policy, out TimeSpan? retention)
    {
        retention = null;
        if (string.IsNullOrWhiteSpace(policy))
        {
            return true;
        }

        if (TryParseObject(policy!) is not { } json)
        {
            return false;
        }

        if (json.Count == 0)
        {
            return true;
        }

        var days = json["MessageRetentionPeriod"] switch
        {
            JsonValue value when value.TryGetValue<int>(out var number) => number,
            JsonValue value when value.TryGetValue<string>(out var text)
                                 && int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => 0,
        };
        if (days is < 1 or > MaxArchiveRetentionDays)
        {
            return false;
        }

        retention = TimeSpan.FromDays(days);
        return true;
    }

    /// <summary>A timestamp as SNS reports one in an attribute: ISO 8601 UTC, to the millisecond.</summary>
    public static string FormatTimestamp(DateTimeOffset timestamp) =>
        timestamp.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
}
