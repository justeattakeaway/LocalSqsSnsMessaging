using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace LocalSqsSnsMessaging;

/// <summary>
/// The validation SQS performs on queue names, queue attributes, tags and permissions, with SQS's
/// error codes and messages.
/// </summary>
internal static class SqsQueueValidation
{
    public const int MaxQueueNameLength = 80;
    public const int MaxTagsPerQueue = 50;
    public const int MaxPolicyStatements = 20;
    private const int MaxTagKeyLength = 128;
    private const int MaxTagValueLength = 256;
    private const int MaxRedriveAllowSourceArns = 10;

    private const string StandardNameMessage =
        "Can only include alphanumeric characters, hyphens, or underscores. 1 to 80 in length";

    private const string FifoNameMessage =
        "The name of a FIFO queue can only include alphanumeric characters, hyphens, or underscores, must end with .fifo suffix and be 1 to 80 in length.";

    /// <summary>Integer attributes and their inclusive bounds.</summary>
    private static readonly Dictionary<string, (int Min, int Max)> IntegerAttributes = new(StringComparer.Ordinal)
    {
        [InternalQueueAttributeName.DelaySeconds] = (0, 900),
        [InternalQueueAttributeName.MaximumMessageSize] = (1_024, 1_048_576),
        [InternalQueueAttributeName.MessageRetentionPeriod] = (60, 1_209_600),
        [InternalQueueAttributeName.ReceiveMessageWaitTimeSeconds] = (0, 20),
        [InternalQueueAttributeName.VisibilityTimeout] = (0, 43_200),
        [InternalQueueAttributeName.KmsDataKeyReusePeriodSeconds] = (60, 86_400),
    };

    /// <summary>Attributes any queue may set.</summary>
    private static readonly HashSet<string> CommonAttributes = new(StringComparer.Ordinal)
    {
        InternalQueueAttributeName.DelaySeconds,
        InternalQueueAttributeName.MaximumMessageSize,
        InternalQueueAttributeName.MessageRetentionPeriod,
        InternalQueueAttributeName.Policy,
        InternalQueueAttributeName.ReceiveMessageWaitTimeSeconds,
        InternalQueueAttributeName.RedrivePolicy,
        InternalQueueAttributeName.RedriveAllowPolicy,
        InternalQueueAttributeName.VisibilityTimeout,
        InternalQueueAttributeName.KmsMasterKeyId,
        InternalQueueAttributeName.KmsDataKeyReusePeriodSeconds,
        InternalQueueAttributeName.SqsManagedSseEnabled,
    };

    /// <summary>Attributes only a FIFO queue may set.</summary>
    private static readonly HashSet<string> FifoAttributes = new(StringComparer.Ordinal)
    {
        InternalQueueAttributeName.FifoQueue,
        InternalQueueAttributeName.ContentBasedDeduplication,
        InternalQueueAttributeName.DeduplicationScope,
        InternalQueueAttributeName.FifoThroughputLimit,
    };

    /// <summary>Attributes SQS computes, which can be read but not set.</summary>
    private static readonly HashSet<string> ReadOnlyAttributes = new(StringComparer.Ordinal)
    {
        InternalQueueAttributeName.ApproximateNumberOfMessages,
        InternalQueueAttributeName.ApproximateNumberOfMessagesDelayed,
        InternalQueueAttributeName.ApproximateNumberOfMessagesNotVisible,
        InternalQueueAttributeName.CreatedTimestamp,
        InternalQueueAttributeName.LastModifiedTimestamp,
        InternalQueueAttributeName.QueueArn,
    };

    /// <summary>The actions <c>AddPermission</c> grants, as SQS names them.</summary>
    private static readonly HashSet<string> PermissionActions = new(StringComparer.Ordinal)
    {
        "*", "ChangeMessageVisibility", "ChangeMessageVisibilityBatch", "DeleteMessage", "DeleteMessageBatch",
        "GetQueueAttributes", "GetQueueUrl", "ListDeadLetterSourceQueues", "ListQueueTags", "PurgeQueue",
        "ReceiveMessage", "SendMessage", "SendMessageBatch", "CancelMessageMoveTask", "ListMessageMoveTasks",
        "StartMessageMoveTask",
    };

    public static AwsServiceException InvalidAttributeName(string name) =>
        SqsRequestValidation.Error("InvalidAttributeName", $"Unknown Attribute {name}.");

    public static AwsServiceException InvalidAttributeValue(string name, string? reason = null) =>
        SqsRequestValidation.Error("InvalidAttributeValue",
            reason is null ? $"Invalid value for the parameter {name}." : $"Invalid value for the parameter {name}. Reason: {reason}");

    /// <summary>A queue name: 1 to 80 alphanumerics, hyphens and underscores, including a <c>.fifo</c> suffix for a FIFO queue.</summary>
    public static void ValidateQueueName(string? name, bool fifo)
    {
        var stem = fifo && name?.EndsWith(".fifo", StringComparison.Ordinal) == true ? name[..^".fifo".Length] : name;
        var valid = !string.IsNullOrEmpty(stem)
                    && name!.Length <= MaxQueueNameLength
                    && (!fifo || stem != name)
                    && stem!.All(c => c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '-' or '_');
        if (!valid)
        {
            throw SqsRequestValidation.InvalidParameterValue(fifo ? FifoNameMessage : StandardNameMessage);
        }
    }

    /// <summary>Whether <c>CreateQueue</c> asks for a FIFO queue.</summary>
    public static bool RequestsFifo(Dictionary<string, string>? attributes) =>
        attributes?.TryGetValue(InternalQueueAttributeName.FifoQueue, out var fifo) == true
        && string.Equals(fifo, "true", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Checks the names and values of attributes being set on a queue (on <c>CreateQueue</c> or
    /// <c>SetQueueAttributes</c>), without checking the redrive policies, which need the bus.
    /// </summary>
    /// <param name="current">The queue's current attributes, which the new ones are combined with; empty on create.</param>
    public static void ValidateAttributes(Dictionary<string, string> attributes, bool fifo, bool creating, IReadOnlyDictionary<string, string> current)
    {
        foreach (var name in attributes.Keys)
        {
            var known = CommonAttributes.Contains(name) || (fifo && FifoAttributes.Contains(name));
            // A standard queue may be created with FifoQueue=false.
            var standardFifoFalse = creating && !fifo && name == InternalQueueAttributeName.FifoQueue;
            if (!known && !standardFifoFalse)
            {
                throw InvalidAttributeName(name);
            }
        }

        foreach (var (name, value) in attributes)
        {
            if (IntegerAttributes.TryGetValue(name, out var bounds))
            {
                if (!int.TryParse(value, NumberStyles.AllowLeadingSign, NumberFormatInfo.InvariantInfo, out var number)
                    || number < bounds.Min || number > bounds.Max)
                {
                    throw InvalidAttributeValue(name);
                }
            }
            else if (name is InternalQueueAttributeName.FifoQueue or InternalQueueAttributeName.ContentBasedDeduplication
                     or InternalQueueAttributeName.SqsManagedSseEnabled)
            {
                if (!bool.TryParse(value, out _))
                {
                    throw InvalidAttributeValue(name);
                }
            }
            else if (name == InternalQueueAttributeName.Policy)
            {
                if (value.Length > 0 && TryParseObject(value) is null)
                {
                    throw InvalidAttributeValue(name);
                }
            }
            else if (name == InternalQueueAttributeName.DeduplicationScope && value is not ("messageGroup" or "queue"))
            {
                throw InvalidAttributeValue(name);
            }
            else if (name == InternalQueueAttributeName.FifoThroughputLimit && value is not ("perQueue" or "perMessageGroupId"))
            {
                throw InvalidAttributeValue(name);
            }
        }

        if (fifo)
        {
            var throughputLimit = attributes.GetValueOrDefault(InternalQueueAttributeName.FifoThroughputLimit)
                                  ?? current.GetValueOrDefault(InternalQueueAttributeName.FifoThroughputLimit);
            var deduplicationScope = attributes.GetValueOrDefault(InternalQueueAttributeName.DeduplicationScope)
                                     ?? current.GetValueOrDefault(InternalQueueAttributeName.DeduplicationScope);
            if (throughputLimit == "perMessageGroupId" && deduplicationScope is not (null or "messageGroup"))
            {
                throw InvalidAttributeValue(InternalQueueAttributeName.FifoThroughputLimit,
                    "To set FifoThroughputLimit to perMessageGroupId, the DeduplicationScope must be messageGroup.");
            }
        }

        if (attributes.TryGetValue(InternalQueueAttributeName.RedriveAllowPolicy, out var redriveAllowPolicy))
        {
            ValidateRedriveAllowPolicy(redriveAllowPolicy);
        }
    }

    /// <summary>Names <c>GetQueueAttributes</c> accepts: <c>All</c> and every attribute a queue can have.</summary>
    public static void ValidateAttributeNamesToGet(IEnumerable<string> names)
    {
        foreach (var name in names)
        {
            if (name != "All" && !CommonAttributes.Contains(name) && !FifoAttributes.Contains(name) && !ReadOnlyAttributes.Contains(name))
            {
                throw InvalidAttributeName(name);
            }
        }
    }

    /// <summary>A parsed <c>RedrivePolicy</c>.</summary>
    public readonly record struct RedrivePolicy(string DeadLetterTargetArn, int MaxReceiveCount);

    /// <summary>Parses and range-checks a <c>RedrivePolicy</c>; whether its target exists is the caller's to check.</summary>
    public static RedrivePolicy ParseRedrivePolicy(string value)
    {
        AwsServiceException Invalid(string reason) =>
            SqsRequestValidation.InvalidParameterValue("RedrivePolicy", value, reason);

        var policy = TryParseObject(value) ?? throw Invalid("Redrive policy is not a valid JSON map.");
        if (policy["deadLetterTargetArn"]?.GetValueKind() is not JsonValueKind.String)
        {
            throw Invalid("Redrive policy does not contain mandatory attribute: deadLetterTargetArn.");
        }

        var countNode = policy["maxReceiveCount"] ?? throw Invalid("Redrive policy does not contain mandatory attribute: maxReceiveCount.");
        var countText = countNode.GetValueKind() == JsonValueKind.String ? countNode.GetValue<string>() : countNode.ToJsonString();
        if (!int.TryParse(countText, NumberStyles.AllowLeadingSign, NumberFormatInfo.InvariantInfo, out var count) || count is < 1 or > 1000)
        {
            throw Invalid($"Invalid value for maxReceiveCount: {countText}, valid values are from 1 to 1000 both inclusive.");
        }

        return new RedrivePolicy(policy["deadLetterTargetArn"]!.GetValue<string>(), count);
    }

    /// <summary>Whether the dead-letter queue's <c>RedriveAllowPolicy</c> lets <paramref name="sourceArn"/> use it.</summary>
    public static bool RedriveAllowed(SqsQueueResource deadLetterQueue, string sourceArn)
    {
        if (deadLetterQueue.Attributes?.TryGetValue(InternalQueueAttributeName.RedriveAllowPolicy, out var allowPolicy) != true
            || TryParseObject(allowPolicy!) is not { } policy)
        {
            return true;
        }

        return policy["redrivePermission"]?.GetValue<string>() switch
        {
            "denyAll" => false,
            "byQueue" => policy["sourceQueueArns"] is JsonArray arns
                         && arns.Any(arn => string.Equals(arn?.GetValue<string>(), sourceArn, StringComparison.Ordinal)),
            _ => true,
        };
    }

    public static AwsServiceException RedriveNotAllowed(RedrivePolicy policy) =>
        SqsRequestValidation.InvalidParameterValue("RedrivePolicy",
            $"RedrivePolicy(maxReceiveCount={policy.MaxReceiveCount}, deadLetterTargetArn={policy.DeadLetterTargetArn})",
            "The value for the dead-letter queue's RedriveAllowPolicy attribute prevents you from completing this request.");

    private static void ValidateRedriveAllowPolicy(string value)
    {
        AwsServiceException Invalid(string reason) =>
            SqsRequestValidation.InvalidParameterValue("RedriveAllowPolicy", value,
                $"Amazon SQS can't create the redrive allow policy. {reason}");

        var unsupported = InvalidAttributeValue(InternalQueueAttributeName.RedriveAllowPolicy,
            "Amazon SQS can't create the redrive allow policy, as it\u2019s in an unsupported format.");
        var policy = TryParseObject(value) ?? throw unsupported;
        var permission = policy["redrivePermission"]?.GetValueKind() == JsonValueKind.String
            ? policy["redrivePermission"]!.GetValue<string>()
            : null;
        var sourceArnCount = (policy["sourceQueueArns"] as JsonArray)?.Count ?? 0;
        switch (permission)
        {
            case "byQueue" when sourceArnCount > MaxRedriveAllowSourceArns:
                throw Invalid($"The maximum number of source queue ARNs you can specify is {MaxRedriveAllowSourceArns}.");
            case "byQueue" when sourceArnCount == 0:
                throw Invalid("When you specify the byQueue permission type, you must also specify one or more sourceQueueArns values.");
            case "allowAll" or "denyAll" when sourceArnCount > 0:
                throw Invalid("To specify one or more source queue ARNs, you must set redrivePermission to byQueue.");
            case "byQueue" or "allowAll" or "denyAll":
                return;
            default:
                throw unsupported;
        }
    }

    /// <summary>
    /// Whether a requested attribute value matches the queue's current one, as <c>CreateQueue</c>
    /// on an existing name compares them: numbers and booleans by value, policies as documents
    /// (ignoring statement ids).
    /// </summary>
    public static bool SameAttributeValue(string name, string requested, string? current)
    {
        if (current is null)
        {
            return false;
        }

        if (IntegerAttributes.ContainsKey(name))
        {
            return int.TryParse(requested, NumberStyles.AllowLeadingSign, NumberFormatInfo.InvariantInfo, out var a)
                   && int.TryParse(current, NumberStyles.AllowLeadingSign, NumberFormatInfo.InvariantInfo, out var b)
                   && a == b;
        }

        if (bool.TryParse(requested, out var requestedFlag) && bool.TryParse(current, out var currentFlag))
        {
            return requestedFlag == currentFlag;
        }

        if (name is InternalQueueAttributeName.Policy or InternalQueueAttributeName.RedrivePolicy or InternalQueueAttributeName.RedriveAllowPolicy)
        {
            var requestedDocument = TryParseObject(requested);
            var currentDocument = TryParseObject(current);
            if (requestedDocument is not null && currentDocument is not null)
            {
                return JsonNode.DeepEquals(Canonical(requestedDocument), Canonical(currentDocument));
            }
        }

        return string.Equals(requested, current, StringComparison.Ordinal);
    }

    /// <summary>A policy document with statement ids dropped and numeric strings read as numbers.</summary>
    private static JsonObject Canonical(JsonObject document)
    {
        var copy = document.DeepClone().AsObject();
        if (copy["Statement"] is JsonArray statements)
        {
            foreach (var statement in statements.OfType<JsonObject>())
            {
                statement.Remove("Sid");
            }
        }

        if (copy["maxReceiveCount"] is JsonValue count && count.GetValueKind() == JsonValueKind.String
            && int.TryParse(count.GetValue<string>(), NumberStyles.None, NumberFormatInfo.InvariantInfo, out var number))
        {
            copy["maxReceiveCount"] = number;
        }

        return copy;
    }

    /// <summary>Tag keys of 1 to 128 characters, not in the reserved <c>aws:</c> namespace; values of up to 256.</summary>
    public static void ValidateTags(Dictionary<string, string>? tags)
    {
        if (tags is not { Count: > 0 })
        {
            throw SqsRequestValidation.MissingParameter("Tags");
        }

        foreach (var (key, value) in tags)
        {
            ValidateTagKey(key);
            if (key.StartsWith("aws:", StringComparison.OrdinalIgnoreCase))
            {
                throw SqsRequestValidation.InvalidParameterValue("Caller is an end user and not allowed to mutate system tags");
            }

            if (value is null)
            {
                throw SqsRequestValidation.InvalidParameterValue("the parameter 'value' may not be null");
            }

            if (value.Length > MaxTagValueLength)
            {
                throw SqsRequestValidation.InvalidParameterValue(
                    $"Tag values must be between 0 and {MaxTagValueLength} characters in length.");
            }
        }
    }

    public static void ValidateTagKey(string? key)
    {
        if (key is not { Length: > 0 and <= MaxTagKeyLength })
        {
            throw SqsRequestValidation.InvalidParameterValue(
                $"Tag keys must be between 1 and {MaxTagKeyLength} characters in length.");
        }
    }

    public static AwsServiceException TooManyTags(string queueName) =>
        SqsRequestValidation.InvalidParameterValue($"Too many tags added for queue {queueName}.");

    public static void ValidatePermissionActions(List<string>? actions)
    {
        foreach (var action in actions ?? [])
        {
            if (!PermissionActions.Contains(action))
            {
                throw SqsRequestValidation.InvalidParameterValue("ActionName", $"SQS:{action}",
                    "Please refer to the appropriate WSDL for a list of valid actions.");
            }
        }
    }

    public static JsonObject? TryParseObject(string value)
    {
        try
        {
            return JsonNode.Parse(value) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
