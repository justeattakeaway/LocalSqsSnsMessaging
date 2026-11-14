using LocalSqsSnsMessaging.Sns.Model;

namespace LocalSqsSnsMessaging;

/// <summary>
/// The request validation SNS performs before publishing, with SNS's error codes and messages.
/// <c>PublishBatch</c> applies the single-message rules to every entry and refuses the whole
/// request when any entry breaks one, so nothing of an invalid batch is delivered.
/// </summary>
internal static class SnsPublishValidation
{
    private const int MaxBatchEntries = 10;
    private const int MaxBatchEntryIdLength = 80;
    private const int MaxSubjectLength = 100;
    private const int MaxFifoTokenLength = 128;
    private const int MaxAttributeNameBytes = 256;
    private const int MaxNumberPrecisionDigits = 38;

    /// <summary>The decimal exponent of the smallest magnitude a Number attribute may have: 10^-128.</summary>
    private const int MinNumberExponent = -128;

    /// <summary>The decimal exponent of the smallest magnitude a Number attribute may NOT have: 10^126.</summary>
    private const int MaxNumberExponentExclusive = 126;

    /// <summary>
    /// A <c>Publish</c> names its topic by exactly one of <c>TopicArn</c> and <c>TargetArn</c>.
    /// </summary>
    /// <returns>The ARN of the topic to publish to.</returns>
    /// <exception cref="NotSupportedException">The request publishes to a phone number (SMS).</exception>
    public static string ResolveTarget(PublishRequest request)
    {
        var hasTopic = !string.IsNullOrEmpty(request.TopicArn);
        var hasTarget = !string.IsNullOrEmpty(request.TargetArn);
        if (hasTopic && hasTarget)
        {
            throw new InternalInvalidParameterException(
                "Invalid parameter: TopicArn Reason: Both TopicArn and TargetArn specified. Use only one or the other.");
        }

        if (hasTopic || hasTarget)
        {
            return hasTopic ? request.TopicArn! : request.TargetArn!;
        }

        if (!string.IsNullOrEmpty(request.PhoneNumber))
        {
            throw new NotSupportedException("Publishing to a phone number (SMS) is not supported.");
        }

        throw new InternalInvalidParameterException(
            "Invalid parameter: TopicArn or TargetArn Reason: no value for required parameter");
    }

    /// <summary>
    /// The checks a <c>PublishBatch</c> gets before its entries are: one to ten entries, each with
    /// an Id of at most 80 alphanumeric, hyphen or underscore characters that no other entry repeats.
    /// </summary>
    public static void ValidateBatch(List<PublishBatchRequestEntry>? entries)
    {
        if (entries is not { Count: > 0 })
        {
            throw Error("EmptyBatchRequest", "The batch request doesn't contain any entries.");
        }

        if (entries.Count > MaxBatchEntries)
        {
            throw Error("TooManyEntriesInBatchRequest", "The batch request contains more entries than permissible.");
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            var id = entry.Id ?? string.Empty;
            if (id.Length > MaxBatchEntryIdLength)
            {
                throw Error("InvalidBatchEntryId", $"The Id of a batch entry in the batch request is too long: {id}");
            }

            if (id.Length == 0 || !id.All(c => IsAsciiLetterOrDigit(c) || c is '-' or '_'))
            {
                throw Error("InvalidBatchEntryId",
                    $"The Id of a batch entry in the batch request contains an impermissible character: {id}");
            }

            if (!seen.Add(id))
            {
                throw Error("BatchEntryIdsNotDistinct", "Two or more batch entries in the request have the same Id.");
            }
        }
    }

    /// <summary>
    /// The checks one message gets, whether it is a <c>Publish</c> or an entry of a
    /// <c>PublishBatch</c>: a non-empty body (that parses, for <c>MessageStructure=json</c>), a
    /// well-formed <c>Subject</c>, well-formed message attributes, and FIFO parameters that suit
    /// <paramref name="topic"/>.
    /// </summary>
    public static void ValidateMessage(
        SnsTopicResource topic,
        string? message,
        string? subject,
        string? messageStructure,
        Dictionary<string, MessageAttributeValue>? attributes,
        string? messageGroupId,
        string? messageDeduplicationId)
    {
        if (string.IsNullOrEmpty(message))
        {
            throw new InternalInvalidParameterException("Invalid parameter: Empty message");
        }

        if (subject is not null && !IsValidSubject(subject))
        {
            throw new InternalInvalidParameterException("Invalid parameter: Subject");
        }

        foreach (var (name, value) in attributes ?? [])
        {
            ValidateAttributeName(name);
            ValidateAttributeValue(name, value);
        }

        ValidateFifoParameters(topic, messageGroupId, messageDeduplicationId);

        // Only for its errors: delivery reads the structure again for each subscription.
        SnsMessageStructure.Parse(message!, messageStructure);
    }

    /// <summary>
    /// The size SNS counts against a topic's <c>MaximumMessageSize</c>: the body's UTF-8 bytes plus
    /// each attribute's name, data type and value. The <c>Subject</c> is not counted.
    /// </summary>
    public static int MessageSize(string? message, Dictionary<string, MessageAttributeValue>? attributes)
    {
        var size = Encoding.UTF8.GetByteCount(message ?? string.Empty);
        foreach (var (name, value) in attributes ?? [])
        {
            size += Encoding.UTF8.GetByteCount(name);
            size += Encoding.UTF8.GetByteCount(value.DataType ?? string.Empty);
            if (value.BinaryValue is not null)
            {
                size += (int)value.BinaryValue.Length;
            }
            else if (value.StringValue is not null)
            {
                size += Encoding.UTF8.GetByteCount(value.StringValue);
            }
        }

        return size;
    }

    /// <summary>
    /// A subject of at most 100 characters, not blank, with no line breaks or other control
    /// characters (a tab is allowed). Leading and trailing spaces are kept as sent.
    /// </summary>
    private static bool IsValidSubject(string subject)
    {
        if (string.IsNullOrWhiteSpace(subject))
        {
            return false;
        }

        var characters = 0;
        for (var i = 0; i < subject.Length; i++)
        {
            var c = subject[i];
            if (char.IsControl(c) && c != '\t')
            {
                return false;
            }

            // A surrogate pair is one character.
            if (!char.IsLowSurrogate(c) || i == 0 || !char.IsHighSurrogate(subject[i - 1]))
            {
                characters++;
            }
        }

        return characters <= MaxSubjectLength;
    }

    /// <summary>
    /// A FIFO topic needs a <c>MessageGroupId</c>, and a <c>MessageDeduplicationId</c> unless it
    /// deduplicates by content; a standard topic accepts a <c>MessageGroupId</c> but no
    /// <c>MessageDeduplicationId</c>. Either id is 1 to 128 printable ASCII characters.
    /// </summary>
    private static void ValidateFifoParameters(SnsTopicResource topic, string? messageGroupId, string? messageDeduplicationId)
    {
        if (messageGroupId is not null)
        {
            ValidateFifoToken("MessageGroupId", messageGroupId);
        }
        else if (topic.IsFifo)
        {
            throw new InternalInvalidParameterException(
                "Invalid parameter: The MessageGroupId parameter is required for FIFO topics");
        }

        if (messageDeduplicationId is not null)
        {
            if (!topic.IsFifo)
            {
                throw new InternalInvalidParameterException(
                    "Invalid parameter: MessageDeduplicationId Reason: The request includes MessageDeduplicationId parameter that is not valid for this topic type");
            }

            ValidateFifoToken("MessageDeduplicationId", messageDeduplicationId);
        }
        else if (topic.IsFifo && !HasContentBasedDeduplication(topic))
        {
            throw new InternalInvalidParameterException(
                "Invalid parameter: The topic should either have ContentBasedDeduplication enabled or MessageDeduplicationId provided explicitly");
        }
    }

    private static bool HasContentBasedDeduplication(SnsTopicResource topic)
    {
        lock (topic.SubscriptionGate)
        {
            return topic.Attributes.TryGetValue(SnsTopicAttributes.ContentBasedDeduplication, out var value)
                   && string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
        }
    }

    private static void ValidateFifoToken(string parameter, string value)
    {
        if (value.Length is 0 or > MaxFifoTokenLength || !value.All(c => c is >= '!' and <= '~'))
        {
            throw new InternalInvalidParameterException(
                $"Invalid parameter: The {parameter} parameter can only include alphanumeric and punctuation characters. 1 to {MaxFifoTokenLength} in length");
        }
    }

    /// <summary>
    /// An attribute name is at most 256 bytes of letters, digits, hyphens, underscores and periods,
    /// doesn't start or end with a period or hold two in a row, and doesn't start with the
    /// <c>AWS.</c> or <c>Amazon.</c> prefixes Amazon reserves.
    /// </summary>
    private static void ValidateAttributeName(string name)
    {
        if (Encoding.UTF8.GetByteCount(name) > MaxAttributeNameBytes)
        {
            throw ParameterValueInvalid("Length of message attribute name must be less than 256 bytes.");
        }

        if (name.StartsWith("AWS.", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("Amazon.", StringComparison.OrdinalIgnoreCase))
        {
            throw ParameterValueInvalid("Message attribute names starting with 'AWS.' or 'Amazon.' are reserved for use by Amazon.");
        }

        if (name.StartsWith('.'))
        {
            throw ParameterValueInvalid("Invalid message attribute name starting with character '.' was found.");
        }

        if (name.EndsWith('.'))
        {
            throw ParameterValueInvalid("Invalid message attribute name ending with character '.' was found.");
        }

        if (name.Contains("..", StringComparison.Ordinal))
        {
            throw ParameterValueInvalid("Message attribute name can not have successive '.' character.");
        }

        for (var i = 0; i < name.Length; i++)
        {
            var codePoint = char.IsSurrogatePair(name, i) ? char.ConvertToUtf32(name, i) : name[i];
            if (!char.IsLetterOrDigit(name, i) && name[i] is not ('-' or '_' or '.'))
            {
                throw ParameterValueInvalid(
                    $"Invalid non-alphanumeric character '#x{codePoint:X}' was found in the message attribute name. Can only include alphanumeric characters, hyphens, underscores, or dots.");
            }

            if (codePoint > char.MaxValue)
            {
                i++;
            }
        }
    }

    /// <summary>
    /// An attribute's data type is <c>String</c>, <c>Number</c> or <c>Binary</c>, optionally with a
    /// custom suffix, and its value is non-empty and in the field that type uses. A Number parses
    /// as a decimal of at most 38 significant digits with a magnitude from 10^-128 up to (not
    /// including) 10^126; a String holds no character outside the Basic Multilingual Plane.
    /// </summary>
    private static void ValidateAttributeValue(string name, MessageAttributeValue value)
    {
        if (string.IsNullOrEmpty(value.DataType))
        {
            throw ParameterValueInvalid($"The message attribute '{name}' must contain non-empty message attribute type.");
        }

        var dataType = value.DataType!;
        var dot = dataType.IndexOf('.', StringComparison.Ordinal);
        var baseType = dot < 0 ? dataType : dataType.Substring(0, dot);
        if (baseType is not ("String" or "Number" or "Binary") || dot == dataType.Length - 1)
        {
            throw ParameterValueInvalid(
                $"The message attribute '{name}' has an invalid message attribute type, the set of supported type prefixes is Binary, Number, and String.");
        }

        var isBinary = baseType == "Binary";
        var field = isBinary ? "Binary" : "String";
        if (isBinary ? value.BinaryValue is null && value.StringValue is not null : value.StringValue is null && value.BinaryValue is not null)
        {
            throw ParameterValueInvalid($"The message attribute '{name}' with type '{baseType}' must use field '{field}'.");
        }

        if (isBinary ? value.BinaryValue is not { Length: > 0 } : string.IsNullOrEmpty(value.StringValue))
        {
            throw ParameterValueInvalid(
                $"The message attribute '{name}' must contain non-empty message attribute value for message attribute type '{baseType}'.");
        }

        if (baseType == "Number")
        {
            ValidateNumber(name, value.StringValue!);
        }
        else if (!isBinary && HasSupplementaryCharacter(value.StringValue!))
        {
            throw new InternalInvalidParameterException(
                $"Invalid parameter: MessageAttributes Reason: The message attribute '{name}' contains a character outside the Basic Multilingual Plane.");
        }
    }

    private static void ValidateNumber(string name, string number)
    {
        if (!TryReadDecimal(number, out var significantDigits, out var exponent))
        {
            throw ParameterValueInvalid($"Could not cast message attribute '{name}' value to number.");
        }

        if (significantDigits.Length > MaxNumberPrecisionDigits)
        {
            throw ParameterValueInvalid(
                $"Message attribute '{name}' number value [{number}] must have less than {MaxNumberPrecisionDigits} precision digits.");
        }

        // Zero has no magnitude to bound.
        if (significantDigits.Length > 0 && exponent is < MinNumberExponent or >= MaxNumberExponentExclusive)
        {
            throw ParameterValueInvalid(
                $"Message attribute '{name}' number value [{number}] scale must be 10^{MinNumberExponent} and 10^{MaxNumberExponentExclusive}.");
        }
    }

    /// <summary>
    /// Reads a decimal number, optionally signed and in exponent notation, without rounding it:
    /// <paramref name="significantDigits"/> are its digits from the first non-zero one (empty for
    /// zero), and <paramref name="exponent"/> is the power of ten of that first digit, so
    /// <c>0.0123</c> gives <c>"123"</c> and -2.
    /// </summary>
    private static bool TryReadDecimal(string number, out string significantDigits, out long exponent)
    {
        significantDigits = string.Empty;
        exponent = 0;

        var i = 0;
        if (i < number.Length && number[i] is '+' or '-')
        {
            i++;
        }

        var integerStart = i;
        while (i < number.Length && IsDigit(number[i]))
        {
            i++;
        }

        var integerDigits = number.Substring(integerStart, i - integerStart);
        var fractionDigits = string.Empty;
        if (i < number.Length && number[i] == '.')
        {
            var fractionStart = ++i;
            while (i < number.Length && IsDigit(number[i]))
            {
                i++;
            }

            fractionDigits = number.Substring(fractionStart, i - fractionStart);
        }

        if (integerDigits.Length + fractionDigits.Length == 0)
        {
            return false;
        }

        long writtenExponent = 0;
        if (i < number.Length && number[i] is 'e' or 'E')
        {
            i++;
            var negative = i < number.Length && number[i] == '-';
            if (i < number.Length && number[i] is '+' or '-')
            {
                i++;
            }

            var exponentStart = i;
            while (i < number.Length && IsDigit(number[i]))
            {
                // Saturate: any exponent this large is out of bounds anyway.
                writtenExponent = Math.Min(writtenExponent * 10 + (number[i] - '0'), int.MaxValue);
                i++;
            }

            if (i == exponentStart)
            {
                return false;
            }

            writtenExponent = negative ? -writtenExponent : writtenExponent;
        }

        if (i != number.Length)
        {
            return false;
        }

        var digits = integerDigits + fractionDigits;
        var firstSignificant = 0;
        while (firstSignificant < digits.Length && digits[firstSignificant] == '0')
        {
            firstSignificant++;
        }

        significantDigits = digits.Substring(firstSignificant);
        exponent = writtenExponent + integerDigits.Length - firstSignificant - 1;
        return true;
    }

    private static bool HasSupplementaryCharacter(string value) => value.Any(char.IsSurrogate);

    private static bool IsDigit(char c) => c is >= '0' and <= '9';

    private static bool IsAsciiLetterOrDigit(char c) => c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') || IsDigit(c);

    private static AwsServiceException ParameterValueInvalid(string message) => Error("ParameterValueInvalid", message);

    private static AwsServiceException Error(string code, string message) => new(message) { ErrorCode = code };
}
