using System.Globalization;
using System.Text;
using LocalSqsSnsMessaging.Sqs.Model;

namespace LocalSqsSnsMessaging;

/// <summary>
/// The request validation SQS performs before acting on a message or batch request, with SQS's
/// error codes and messages.
/// </summary>
internal static class SqsRequestValidation
{
    public const int MaxBatchEntries = 10;
    public const int MaxMessageAttributes = 10;
    public const int MaxDelaySeconds = 900;
    public const int MaxVisibilityTimeoutSeconds = 43_200;
    public const int MaxReceiveMessages = 10;
    public const int MaxWaitTimeSeconds = 20;
    private const int MaxFifoTokenLength = 128;
    private const int MaxAttributeNameBytes = 256;
    private const int MaxAttributeTypeBytes = 256;
    private const int MaxNumberPrecisionDigits = 38;
    private const int MaxBatchEntryIdLength = 80;

    private const string AllowedBodyCharacters = "#x9 | #xA | #xD | [#x20-#xD7FF] | [#xE000-#xFFFD] | [#x10000-#x10FFFF]";

    public static AwsServiceException Error(string code, string message) => new(message) { ErrorCode = code };

    public static AwsServiceException InvalidParameterValue(string message) => Error("InvalidParameterValue", message);

    public static AwsServiceException InvalidParameterValue(string parameter, object? value, string reason) =>
        InvalidParameterValue($"Value {value} for parameter {parameter} is invalid. Reason: {reason}");

    public static AwsServiceException MissingParameter(string parameter) =>
        Error("MissingParameter", $"The request must contain the parameter {parameter}.");

    /// <summary>
    /// The checks every batch request gets before any entry is processed: at least one and at most
    /// ten entries, each with a well-formed Id that no other entry repeats.
    /// </summary>
    /// <param name="entryType">The entry type SQS names in the empty-batch message, e.g. <c>SendMessageBatchRequestEntry</c>.</param>
    public static void ValidateBatchRequest<TEntry>(List<TEntry>? entries, Func<TEntry, string?> getId, string entryType)
    {
        if (entries is not { Count: > 0 })
        {
            throw Error("EmptyBatchRequest", $"There should be at least one {entryType} in the request.");
        }

        if (entries.Count > MaxBatchEntries)
        {
            throw Error("TooManyEntriesInBatchRequest",
                $"Maximum number of entries per request are {MaxBatchEntries}. You have sent {entries.Count}.");
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            var id = getId(entry);
            if (string.IsNullOrEmpty(id) || id!.Length > MaxBatchEntryIdLength || !IsBatchEntryId(id))
            {
                throw Error("InvalidBatchEntryId",
                    "A batch entry id can only contain alphanumeric characters, hyphens and underscores. It can be at most 80 letters long.");
            }

            if (!seen.Add(id))
            {
                throw Error("BatchEntryIdsNotDistinct", $"Id {id} repeated.");
            }
        }
    }

    private static bool IsBatchEntryId(string id)
    {
        foreach (var c in id)
        {
            if (!(IsAsciiLetterOrDigit(c) || c is '-' or '_'))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Bounds on the parameters of a <c>ReceiveMessage</c> call.</summary>
    public static void ValidateReceive(ReceiveMessageRequest request)
    {
        if (request.MaxNumberOfMessages is { } max and (< 1 or > MaxReceiveMessages))
        {
            throw InvalidParameterValue("MaxNumberOfMessages", max, $"Must be between 1 and {MaxReceiveMessages}, if provided.");
        }

        if (request.WaitTimeSeconds is { } wait and (< 0 or > MaxWaitTimeSeconds))
        {
            throw InvalidParameterValue("WaitTimeSeconds", wait, $"Must be >= 0 and <= {MaxWaitTimeSeconds}, if provided.");
        }

        if (request.VisibilityTimeout is { } visibility)
        {
            ValidateVisibilityTimeout(visibility);
        }

        if (request.ReceiveRequestAttemptId is { } attemptId)
        {
            ValidateFifoToken("ReceiveRequestAttemptId", attemptId);
        }
    }

    public static void ValidateVisibilityTimeout(int visibilityTimeout)
    {
        if (visibilityTimeout is < 0 or > MaxVisibilityTimeoutSeconds)
        {
            throw InvalidParameterValue("VisibilityTimeout", visibilityTimeout,
                $"VisibilityTimeout must be an integer between 0 and {MaxVisibilityTimeoutSeconds}.");
        }
    }

    /// <summary>A message's visibility can't be extended past 12 hours from when it was received.</summary>
    public static AwsServiceException? CheckVisibilityCap(int visibilityTimeout, TimeSpan sinceReceive)
    {
        return sinceReceive + TimeSpan.FromSeconds(visibilityTimeout) > TimeSpan.FromSeconds(MaxVisibilityTimeoutSeconds)
            ? InvalidParameterValue("VisibilityTimeout", visibilityTimeout,
                $"Total VisibilityTimeout for the message is beyond the limit [{MaxVisibilityTimeoutSeconds} seconds].")
            : null;
    }

    /// <summary>What a send names, single or batched, for <see cref="ValidateSend"/>.</summary>
    public readonly record struct SendParameters(
        string? MessageBody,
        int? DelaySeconds,
        string? MessageGroupId,
        string? MessageDeduplicationId,
        Dictionary<string, MessageAttributeValue>? MessageAttributes,
        Dictionary<string, MessageSystemAttributeValue>? MessageSystemAttributes);

    /// <summary>
    /// Validates one message for <paramref name="queue"/>, as <c>SendMessage</c> does, or as the
    /// entry at zero-based <paramref name="batchIndex"/> of a <c>SendMessageBatch</c>.
    /// </summary>
    /// <returns>The error to report, or <see langword="null"/> when the message is valid.</returns>
    public static AwsServiceException? ValidateSend(SqsQueueResource queue, in SendParameters send, int? batchIndex = null)
    {
        if (string.IsNullOrEmpty(send.MessageBody))
        {
            return batchIndex is { } index
                ? Error("EmptyValue", $"No Value Found for SendMessageBatchRequestEntry.{index + 1}.MessageBody.")
                : MissingParameter("MessageBody");
        }

        if (send.DelaySeconds is { } delay)
        {
            if (delay is < 0 or > MaxDelaySeconds)
            {
                return InvalidParameterValue("DelaySeconds", delay, $"DelaySeconds must be >= 0 and <= {MaxDelaySeconds}.");
            }

            if (queue.IsFifo && delay != 0)
            {
                return NotValidForQueueType("DelaySeconds", delay);
            }
        }

        if (send.MessageGroupId is { } groupId)
        {
            if (ValidateFifoTokenOrNull("MessageGroupId", groupId) is { } groupError)
            {
                return groupError;
            }
        }
        else if (queue.IsFifo)
        {
            return MissingParameter("MessageGroupId");
        }

        if (send.MessageDeduplicationId is { } deduplicationId)
        {
            if (!queue.IsFifo)
            {
                return NotValidForQueueType("MessageDeduplicationId", deduplicationId);
            }

            if (ValidateFifoTokenOrNull("MessageDeduplicationId", deduplicationId) is { } deduplicationError)
            {
                return deduplicationError;
            }
        }
        else if (queue.IsFifo && !queue.ContentBasedDeduplication)
        {
            return InvalidParameterValue(
                "The queue should either have ContentBasedDeduplication enabled or MessageDeduplicationId provided explicitly");
        }

        if (ValidateMessageAttributes(send.MessageAttributes) is { } attributeError)
        {
            return attributeError;
        }

        if (ValidateSystemAttributes(send.MessageSystemAttributes) is { } systemAttributeError)
        {
            return systemAttributeError;
        }

        if (FindInvalidBodyCharacter(send.MessageBody!) is { } invalidCharacter)
        {
            return Error("InvalidMessageContents",
                $"Invalid binary character '#x{invalidCharacter:X}' was found in the message body, the set of allowed characters is {AllowedBodyCharacters}.");
        }

        var size = MessageSize(send.MessageBody!, send.MessageAttributes);
        if (size > queue.MaximumMessageSize)
        {
            var verb = batchIndex is null ? "are invalid" : "cannot be validated";
            return InvalidParameterValue(
                $"One or more parameters {verb}. Reason: Message must be shorter than {queue.MaximumMessageSize} bytes.");
        }

        return null;
    }

    private static AwsServiceException NotValidForQueueType(string parameter, object value) =>
        InvalidParameterValue(parameter, value, "The request include parameter that is not valid for this queue type.");

    /// <summary>
    /// The size SQS counts against <c>MaximumMessageSize</c>: the body's UTF-8 bytes plus each
    /// attribute's name, data type and value. System attributes are not counted.
    /// </summary>
    public static int MessageSize(string messageBody, Dictionary<string, MessageAttributeValue>? messageAttributes)
    {
        var totalSize = Encoding.UTF8.GetByteCount(messageBody);
        foreach (var (name, value) in messageAttributes ?? [])
        {
            totalSize += Encoding.UTF8.GetByteCount(name);
            totalSize += Encoding.UTF8.GetByteCount(value.DataType ?? string.Empty);
            if (value.BinaryValue is not null)
            {
                totalSize += (int)value.BinaryValue.Length;
            }
            else if (value.StringValue is not null)
            {
                totalSize += Encoding.UTF8.GetByteCount(value.StringValue);
            }
        }

        return totalSize;
    }

    public static void ValidateFifoToken(string parameter, string value)
    {
        if (ValidateFifoTokenOrNull(parameter, value) is { } error)
        {
            throw error;
        }
    }

    /// <summary>MessageGroupId, MessageDeduplicationId and ReceiveRequestAttemptId: 1 to 128 printable ASCII characters.</summary>
    private static AwsServiceException? ValidateFifoTokenOrNull(string parameter, string value)
    {
        var valid = value.Length is > 0 and <= MaxFifoTokenLength;
        foreach (var c in value)
        {
            valid &= c is >= '!' and <= '~';
        }

        return valid
            ? null
            : InvalidParameterValue(parameter, value,
                $"{parameter} can only include alphanumeric and punctuation characters. 1 to {MaxFifoTokenLength} in length.");
    }

    /// <summary>The first character outside the XML 1.0 character set SQS allows in a body, if any.</summary>
    private static int? FindInvalidBodyCharacter(string body)
    {
        for (var i = 0; i < body.Length; i++)
        {
            var c = body[i];
            if (char.IsHighSurrogate(c) && i + 1 < body.Length && char.IsLowSurrogate(body[i + 1]))
            {
                // Every supplementary-plane code point (#x10000-#x10FFFF) is allowed.
                i++;
                continue;
            }

            if (!(c is '\t' or '\n' or '\r' or (>= '\x20' and <= '퟿') or (>= '' and <= '�')))
            {
                return c;
            }
        }

        return null;
    }

    private static AwsServiceException? ValidateMessageAttributes(Dictionary<string, MessageAttributeValue>? attributes)
    {
        if (attributes is null)
        {
            return null;
        }

        if (attributes.Count > MaxMessageAttributes)
        {
            return InvalidParameterValue(
                $"Number of message attributes [{attributes.Count}] exceeds the allowed maximum [{MaxMessageAttributes}].");
        }

        foreach (var (name, value) in attributes)
        {
            if ((ValidateAttributeName(name) ?? ValidateAttributeValue(name, value)) is { } error)
            {
                return error;
            }
        }

        return null;
    }

    private static AwsServiceException? ValidateAttributeName(string name)
    {
        if (Encoding.UTF8.GetByteCount(name) > MaxAttributeNameBytes)
        {
            return InvalidParameterValue($"Message (user) attribute name must be shorter than {MaxAttributeNameBytes} bytes.");
        }

        if (name.StartsWith("AWS.", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("Amazon.", StringComparison.OrdinalIgnoreCase))
        {
            return InvalidParameterValue(
                "You can't use message attribute names beginning with 'AWS.' or 'Amazon'. These strings are reserved for internal use.");
        }

        if (name.StartsWith('.'))
        {
            return InvalidParameterValue("Message (user) attribute name must not begin with character '.'.");
        }

        if (name.EndsWith('.'))
        {
            return InvalidParameterValue("Message (user) attribute name must not end with character '.'.");
        }

        if (name.Contains("..", StringComparison.Ordinal))
        {
            return InvalidParameterValue("Message (user) attribute name must not have successive '.' characters.");
        }

        foreach (var c in name)
        {
            if (!(IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.'))
            {
                return InvalidParameterValue(
                    $"Message (user) attribute name contains invalid non-alphanumeric character '#x{(int)c:X}'. You can use only alphanumeric characters, hyphens (-), underscores (_), or periods (.).");
            }
        }

        return null;
    }

    private static AwsServiceException? ValidateAttributeValue(string name, MessageAttributeValue value)
    {
        var dataType = value.DataType ?? string.Empty;
        if (Encoding.UTF8.GetByteCount(dataType) > MaxAttributeTypeBytes)
        {
            return InvalidParameterValue($"Message (user) attribute type must be shorter than {MaxAttributeTypeBytes} bytes.");
        }

        // A data type is String, Number or Binary, optionally followed by '.' and a non-empty custom label.
        var dot = dataType.IndexOf('.', StringComparison.Ordinal);
        var baseType = dot < 0 ? dataType : dataType[..dot];
        if (baseType is not ("String" or "Number" or "Binary") || dot == dataType.Length - 1)
        {
            return InvalidParameterValue(
                $"The type of message (user) attribute '{name}' is invalid. You must use only the following supported type prefixes: Binary, Number, String.");
        }

        var isBinary = baseType == "Binary";
        var field = isBinary ? "Binary" : "String";
        var hasString = value.StringValue is not null;
        var hasBinary = value.BinaryValue is not null;
        if (isBinary ? hasString && !hasBinary : hasBinary && !hasString)
        {
            return InvalidParameterValue($"Message (user) attribute '{name}' with type '{baseType}' must use field '{field}'.");
        }

        if (isBinary ? value.BinaryValue is not { Length: > 0 } : string.IsNullOrEmpty(value.StringValue))
        {
            return InvalidParameterValue($"Message (user) attribute '{name}' must contain a non-empty value of type '{field}'.");
        }

        if (baseType == "Number")
        {
            var number = value.StringValue!;
            if (!IsNumber(number))
            {
                return InvalidParameterValue($"Can't cast the value of message (user) attribute '{name}' to a number.");
            }

            if (PrecisionDigits(number) > MaxNumberPrecisionDigits)
            {
                return InvalidParameterValue(
                    $"Message (user) attribute '{name}' number value [{number}] must contain fewer than {MaxNumberPrecisionDigits} precision digits.");
            }
        }

        return null;
    }

    /// <summary>The significant digits of a number in <see cref="IsNumber"/> form.</summary>
    private static int PrecisionDigits(string number)
    {
        var exponent = number.IndexOfAny(['e', 'E']);
        var mantissa = exponent < 0 ? number : number[..exponent];
        var digits = mantissa.TrimStart('+', '-').Replace(".", string.Empty, StringComparison.Ordinal).TrimStart('0');
        return Math.Max(digits.Length, 1);
    }

    /// <summary>
    /// A Number attribute as SQS stores it: exponent notation is written out in plain notation
    /// (<c>1e3</c> reads back as <c>1000</c>); any other form is kept as sent.
    /// </summary>
    public static string NormalizeNumber(string number)
    {
        if (number.IndexOfAny(['e', 'E']) < 0)
        {
            return number;
        }

        return decimal.TryParse(number, NumberStyles.Float, NumberFormatInfo.InvariantInfo, out var value)
            ? value.ToString(NumberFormatInfo.InvariantInfo)
            : number;
    }

    private static AwsServiceException? ValidateSystemAttributes(Dictionary<string, MessageSystemAttributeValue>? attributes)
    {
        foreach (var (name, value) in attributes ?? [])
        {
            if (!string.Equals(name, InternalMessageSystemAttributeName.AWSTraceHeader, StringComparison.Ordinal))
            {
                return InvalidParameterValue($"Message system attribute name '{name}' is invalid.");
            }

            if (value.DataType is null or "String" && string.IsNullOrEmpty(value.StringValue))
            {
                return InvalidParameterValue(
                    $"Message system attribute '{name}' must contain a non-empty value of type 'String'.");
            }
        }

        return null;
    }

    /// <summary>
    /// Whether an <c>AWSTraceHeader</c> is kept on the message. SQS accepts but silently drops a
    /// header that is not a String or does not parse as an X-Ray trace header.
    /// </summary>
    public static bool IsWellFormedTraceHeader(MessageSystemAttributeValue value)
    {
        if (value.DataType is not (null or "String") || value.StringValue is null)
        {
            return false;
        }

        foreach (var part in value.StringValue.Split(';'))
        {
            if (part.StartsWith("Root=", StringComparison.Ordinal))
            {
                return IsTraceRoot(part["Root=".Length..]);
            }
        }

        return false;
    }

    private static bool IsAsciiLetterOrDigit(char c) => c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9');

    /// <summary>A decimal number, optionally signed and in exponent notation, with no surrounding whitespace.</summary>
    private static bool IsNumber(string value)
    {
        var i = 0;
        if (i < value.Length && value[i] is '+' or '-')
        {
            i++;
        }

        var digits = CountDigits(value, ref i);
        if (i < value.Length && value[i] == '.')
        {
            i++;
            digits += CountDigits(value, ref i);
        }

        if (digits == 0)
        {
            return false;
        }

        if (i < value.Length && value[i] is 'e' or 'E')
        {
            i++;
            if (i < value.Length && value[i] is '+' or '-')
            {
                i++;
            }

            if (CountDigits(value, ref i) == 0)
            {
                return false;
            }
        }

        return i == value.Length;
    }

    private static int CountDigits(string value, ref int index)
    {
        var start = index;
        while (index < value.Length && value[index] is >= '0' and <= '9')
        {
            index++;
        }

        return index - start;
    }

    /// <summary>An X-Ray trace id: <c>1-</c>, 8 hex digits, <c>-</c>, 24 hex digits.</summary>
    private static bool IsTraceRoot(string root)
    {
        if (root.Length != 35 || !root.StartsWith("1-", StringComparison.Ordinal) || root[10] != '-')
        {
            return false;
        }

        for (var i = 2; i < root.Length; i++)
        {
            if (i != 10 && !Uri.IsHexDigit(root[i]))
            {
                return false;
            }
        }

        return true;
    }
}
