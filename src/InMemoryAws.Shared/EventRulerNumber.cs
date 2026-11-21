using System.Globalization;
using System.Numerics;

namespace InMemoryAws;

/// <summary>
/// Which JSON numbers Event Ruler can compare: those a binary64 double holds exactly as written.
/// </summary>
/// <remarks>
/// Event Ruler reads a number for a numeric comparison as a double, and keeps it only when the
/// double's shortest decimal form is the number the JSON spelled. Above 2^53 a double holds only
/// the even integers, so <c>9007199254740994</c> is comparable and <c>9007199254740993</c> is not:
/// a <c>numeric</c> (or numeric <c>anything-but</c>) operand like it is refused with
/// "Cannot compare number", and an event number like it is never matched by a numeric comparison,
/// though exact-value matching and <c>exists</c> still see it.
/// </remarks>
internal static class EventRulerNumber
{
    /// <summary>Whether <paramref name="text"/> (a JSON number) is finite and survives a round trip through a double.</summary>
    public static bool IsComparable(string text)
    {
        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            || double.IsNaN(value) || double.IsInfinity(value))
        {
            return false;
        }

        return TryNormalize(text, out var mantissa, out var exponent)
            && TryNormalize(value.ToString("R", CultureInfo.InvariantCulture), out var roundTripMantissa, out var roundTripExponent)
            && mantissa == roundTripMantissa
            && exponent == roundTripExponent;
    }

    /// <summary>A decimal number as mantissa × 10^exponent, with no trailing zeros in the mantissa (zero is 0 × 10^0).</summary>
    private static bool TryNormalize(string text, out BigInteger mantissa, out int exponent)
    {
        mantissa = BigInteger.Zero;
        exponent = 0;
        var negative = false;
        var digits = 0;
        var fractionDigits = 0;
        var inFraction = false;
        var i = 0;
        if (i < text.Length && text[i] is '-' or '+')
        {
            negative = text[i] == '-';
            i++;
        }

        for (; i < text.Length && text[i] is not ('e' or 'E'); i++)
        {
            var c = text[i];
            if (c == '.' && !inFraction)
            {
                inFraction = true;
            }
            else if (c is >= '0' and <= '9')
            {
                mantissa = (mantissa * 10) + (c - '0');
                digits++;
                fractionDigits += inFraction ? 1 : 0;
            }
            else
            {
                return false;
            }
        }

        if (digits == 0)
        {
            return false;
        }

        if (i < text.Length)
        {
            var written = 0L;
            var exponentNegative = false;
            i++;
            if (i < text.Length && text[i] is '-' or '+')
            {
                exponentNegative = text[i] == '-';
                i++;
            }

            if (i == text.Length)
            {
                return false;
            }

            for (; i < text.Length; i++)
            {
                if (text[i] is not (>= '0' and <= '9') || (written = (written * 10) + (text[i] - '0')) > int.MaxValue / 2)
                {
                    return false;
                }
            }

            exponent = (int)(exponentNegative ? -written : written);
        }

        exponent -= fractionDigits;
        if (mantissa.IsZero)
        {
            exponent = 0;
            return true;
        }

        while (mantissa % 10 == 0)
        {
            mantissa /= 10;
            exponent++;
        }

        if (negative)
        {
            mantissa = -mantissa;
        }

        return true;
    }
}
