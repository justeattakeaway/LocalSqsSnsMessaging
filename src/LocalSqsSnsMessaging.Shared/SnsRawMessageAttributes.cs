using LocalSqsSnsMessaging.Sns.Model;
using SqsMessageAttributeValue = LocalSqsSnsMessaging.Sqs.Model.MessageAttributeValue;

namespace LocalSqsSnsMessaging;

/// <summary>
/// How SNS turns a notification's message attributes into SQS message attributes for a raw
/// (<c>RawMessageDelivery</c>) SQS subscription. The JSON envelope of a non-raw delivery carries
/// them verbatim instead.
/// </summary>
internal static class SnsRawMessageAttributes
{
    /// <summary>The most message attributes SQS accepts on a message.</summary>
    public const int MaxSqsAttributes = 10;

    /// <summary>
    /// A raw exponent beyond this is far outside the range SNS accepts for a Number (10^-128 to
    /// 10^126); such a value is passed on as published rather than written out digit by digit.
    /// </summary>
    private const int MaxExponentMagnitude = 1000;

    /// <summary>
    /// The attributes as SQS message attributes, or <see langword="false"/> when SQS would refuse
    /// them (more than <see cref="MaxSqsAttributes"/>), in which case SNS can't deliver the message.
    /// Types and labels are kept (<c>String.Array</c>, <c>Number.float</c>, ...); Number values
    /// are written in canonical form (see <see cref="CanonicalNumber"/>).
    /// </summary>
    public static bool TryConvert(
        Dictionary<string, MessageAttributeValue>? attributes,
        out Dictionary<string, SqsMessageAttributeValue>? sqsAttributes)
    {
        sqsAttributes = null;
        if (attributes is null)
        {
            return true;
        }

        if (attributes.Count > MaxSqsAttributes)
        {
            return false;
        }

        sqsAttributes = new Dictionary<string, SqsMessageAttributeValue>(attributes.Count, StringComparer.Ordinal);
        foreach (var (name, value) in attributes)
        {
            var isNumber = value.DataType is { } dataType
                           && (dataType == "Number" || dataType.StartsWith("Number.", StringComparison.Ordinal));
            sqsAttributes[name] = new SqsMessageAttributeValue
            {
                DataType = value.DataType,
                StringValue = isNumber && value.StringValue is not null ? CanonicalNumber(value.StringValue) : value.StringValue,
                BinaryValue = value.BinaryValue
            };
        }

        return true;
    }

    /// <summary>
    /// A Number in the canonical form a raw delivery carries: plain notation, no <c>+</c> sign, no
    /// leading zeros before the point and no trailing zeros after it (<c>+5</c> is <c>5</c>,
    /// <c>.5</c> is <c>0.5</c>, <c>1.500</c> is <c>1.5</c>, <c>1e3</c> is <c>1000</c>). A value that
    /// isn't a decimal number is returned unchanged.
    /// </summary>
    internal static string CanonicalNumber(string number)
    {
        var i = 0;
        var negative = false;
        if (i < number.Length && number[i] is '+' or '-')
        {
            negative = number[i] == '-';
            i++;
        }

        var digits = new StringBuilder(number.Length);
        var fractionDigits = 0;
        var seenPoint = false;
        for (; i < number.Length && (number[i] is >= '0' and <= '9' || (number[i] == '.' && !seenPoint)); i++)
        {
            if (number[i] == '.')
            {
                seenPoint = true;
                continue;
            }

            digits.Append(number[i]);
            fractionDigits += seenPoint ? 1 : 0;
        }

        var exponent = 0;
        if (i < number.Length && number[i] is 'e' or 'E')
        {
#pragma warning disable CA1846 // Span overloads aren't available on netstandard2.0.
            if (!int.TryParse(number.Substring(i + 1), NumberStyles.AllowLeadingSign, NumberFormatInfo.InvariantInfo, out exponent))
#pragma warning restore CA1846
            {
                return number;
            }

            i = number.Length;
        }

        if (i != number.Length || digits.Length == 0 || Math.Abs((long)exponent) > MaxExponentMagnitude)
        {
            return number;
        }

        // The value is digits × 10^-scale; drop zeros that don't change it.
        var significand = digits.ToString().TrimStart('0');
        var scale = fractionDigits - exponent;
        var trimmed = significand.TrimEnd('0');
        scale -= significand.Length - trimmed.Length;
        significand = trimmed;
        if (significand.Length == 0)
        {
            return "0";
        }

        string plain;
        if (scale <= 0)
        {
            plain = significand + new string('0', -scale);
        }
        else if (significand.Length > scale)
        {
            plain = significand.Insert(significand.Length - scale, ".");
        }
        else
        {
            plain = "0." + new string('0', scale - significand.Length) + significand;
        }

        return negative ? "-" + plain : plain;
    }
}
