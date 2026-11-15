using System.Numerics;

namespace LocalSqsSnsMessaging.DynamoDb;

/// <summary>Why a number's text does not denote a number DynamoDB can store.</summary>
internal enum DynamoNumberFault
{
    None,

    /// <summary>Not <c>[+-]?digits[.digits][eE[+-]?digits]</c>.</summary>
    NotNumeric,

    /// <summary>More than 38 significant digits.</summary>
    Precision,

    /// <summary>A magnitude of 10^126 or more.</summary>
    Overflow,

    /// <summary>A non-zero magnitude below 10^-130.</summary>
    Underflow,
}

/// <summary>
/// A DynamoDB number: up to 38 significant digits, with a magnitude from 10^-130 to below 10^126.
/// Held as <c>0.Digits × 10^Point</c> with no leading or trailing zeros in <see cref="Digits"/>, so
/// each number has one form (<c>1</c>, <c>1.0</c> and <c>10E-1</c> are the same number), which is
/// the form DynamoDB answers with.
/// </summary>
/// <remarks>
/// <see cref="decimal"/> holds only 28 or 29 digits, and <see cref="double"/> fewer, so neither
/// can hold every number DynamoDB stores.
/// </remarks>
internal readonly struct DynamoNumber : IComparable<DynamoNumber>, IEquatable<DynamoNumber>
{
    public const int MaxSignificantDigits = 38;
    private const int MinPoint = -129;
    private const int MaxPoint = 126;
    private const int MinExponent = -130;
    private const int MaxExponent = 125;

    /// <summary>The most digits of text a number may carry before its zeros are normalised away.</summary>
    private const int MaxTextDigits = MaxSignificantDigits + (MaxPoint - MinPoint);

    private DynamoNumber(bool negative, string digits, int point)
    {
        Negative = negative;
        Digits = digits;
        Point = point;
    }

    public static DynamoNumber Zero { get; } = new(false, string.Empty, 0);

    /// <summary>True for a number below zero (zero is never negative).</summary>
    public bool Negative { get; }

    /// <summary>The significant digits, empty for zero.</summary>
    public string Digits { get; } = string.Empty;

    /// <summary>Where the decimal point is: the number is <c>0.Digits × 10^Point</c>.</summary>
    public int Point { get; }

    public bool IsZero => string.IsNullOrEmpty(Digits);

    /// <summary>The number of significant digits.</summary>
    public int SignificantDigits => Digits?.Length ?? 0;

    public static bool TryParse(string? text, out DynamoNumber number) => Parse(text, out number) == DynamoNumberFault.None;

    /// <summary>Parses <paramref name="text"/>, returning why it is not a storable number, if it isn't.</summary>
    public static DynamoNumberFault Parse(string? text, out DynamoNumber number)
    {
        number = Zero;
        if (string.IsNullOrEmpty(text))
        {
            return DynamoNumberFault.NotNumeric;
        }

        var span = text.AsSpan();
        var negative = false;
        if (span[0] == '-')
        {
            negative = true;
            span = span[1..];
        }
        else if (span[0] == '+')
        {
            span = span[1..];
        }

        long exponent = 0;
        var e = span.IndexOfAny('e', 'E');
        if (e >= 0)
        {
            var exponentText = span[(e + 1)..];
            if (exponentText.Length is 0 or > 20 || !long.TryParse(exponentText.ToString(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out exponent))
            {
                return DynamoNumberFault.NotNumeric;
            }

            span = span[..e];
        }

        var dot = span.IndexOf('.');
        var integral = dot >= 0 ? span[..dot] : span;
        var fractional = dot >= 0 ? span[(dot + 1)..] : [];
        if (integral.Length == 0 && fractional.Length == 0)
        {
            return DynamoNumberFault.NotNumeric;
        }

        foreach (var c in integral)
        {
            if (c is < '0' or > '9')
            {
                return DynamoNumberFault.NotNumeric;
            }
        }

        foreach (var c in fractional)
        {
            if (c is < '0' or > '9')
            {
                return DynamoNumberFault.NotNumeric;
            }
        }

        if (integral.Length + fractional.Length > MaxTextDigits)
        {
            return DynamoNumberFault.NotNumeric;
        }

        var all = string.Concat(integral.ToString(), fractional.ToString());
        var leading = 0;
        while (leading < all.Length && all[leading] == '0')
        {
            leading++;
        }

        var trailing = all.Length;
        while (trailing > leading && all[trailing - 1] == '0')
        {
            trailing--;
        }

        var digits = all[leading..trailing];
        if (digits.Length == 0)
        {
            if (exponent > MaxExponent)
            {
                return DynamoNumberFault.Overflow;
            }

            return exponent < MinExponent ? DynamoNumberFault.Underflow : DynamoNumberFault.None;
        }

        // An exponent near the ends of a long saturates rather than wrapping to the other end.
        var offset = (long)integral.Length - leading;
        var point = offset > 0 && exponent > long.MaxValue - offset ? long.MaxValue
            : offset < 0 && exponent < long.MinValue - offset ? long.MinValue
            : exponent + offset;
        if (digits.Length > MaxSignificantDigits)
        {
            return DynamoNumberFault.Precision;
        }

        if (point > MaxPoint)
        {
            return DynamoNumberFault.Overflow;
        }

        if (point < MinPoint)
        {
            return DynamoNumberFault.Underflow;
        }

        number = new DynamoNumber(negative, digits, (int)point);
        return DynamoNumberFault.None;
    }

    /// <summary>The number exactly, as an integer scaled by a power of ten: <c>Mantissa × 10^Scale</c>.</summary>
    private (BigInteger Mantissa, int Scale) Scaled()
    {
        if (IsZero)
        {
            return (BigInteger.Zero, 0);
        }

        var mantissa = BigInteger.Parse(Digits, CultureInfo.InvariantCulture);
        return (Negative ? -mantissa : mantissa, Point - Digits.Length);
    }

    private static DynamoNumberFault FromScaled(BigInteger mantissa, int scale, out DynamoNumber number)
    {
        if (mantissa.IsZero)
        {
            number = Zero;
            return DynamoNumberFault.None;
        }

        var negative = mantissa.Sign < 0;
        var text = BigInteger.Abs(mantissa).ToString(CultureInfo.InvariantCulture);

        // A result past the largest magnitude overflows, however many digits it has.
        if (text.Length + scale > MaxPoint)
        {
            number = Zero;
            return DynamoNumberFault.Overflow;
        }

        var exponent = scale.ToString(CultureInfo.InvariantCulture);
        return Parse((negative ? "-" : "") + text + "E" + exponent, out number);
    }

    /// <summary>The sum, or why it can't be stored.</summary>
    public static DynamoNumberFault Add(DynamoNumber left, DynamoNumber right, out DynamoNumber sum)
    {
        var (a, aScale) = left.Scaled();
        var (b, bScale) = right.Scaled();
        var scale = Math.Min(aScale, bScale);
        a *= BigInteger.Pow(10, aScale - scale);
        b *= BigInteger.Pow(10, bScale - scale);
        return FromScaled(a + b, scale, out sum);
    }

    public DynamoNumber Negate() => IsZero ? this : new DynamoNumber(!Negative, Digits, Point);

    /// <summary>The form DynamoDB answers with: plain decimal notation, no exponent, no redundant zeros.</summary>
    public override string ToString()
    {
        if (IsZero)
        {
            return "0";
        }

        string magnitude;
        if (Point <= 0)
        {
            magnitude = "0." + new string('0', -Point) + Digits;
        }
        else if (Point >= Digits.Length)
        {
            magnitude = Digits + new string('0', Point - Digits.Length);
        }
        else
        {
            magnitude = Digits[..Point] + "." + Digits[Point..];
        }

        return Negative ? "-" + magnitude : magnitude;
    }

    /// <summary>The number as a double, for the few places DynamoDB needs an approximation (TTL).</summary>
    public double ToDouble() => double.Parse(ToString(), CultureInfo.InvariantCulture);

    public int CompareTo(DynamoNumber other)
    {
        if (Negative != other.Negative)
        {
            return Negative ? -1 : 1;
        }

        var magnitude = CompareMagnitude(this, other);
        return Negative ? -magnitude : magnitude;
    }

    private static int CompareMagnitude(DynamoNumber a, DynamoNumber b)
    {
        if (a.IsZero || b.IsZero)
        {
            return (a.IsZero ? 0 : 1) - (b.IsZero ? 0 : 1);
        }

        var point = a.Point.CompareTo(b.Point);
        return point != 0 ? point : string.CompareOrdinal(a.Digits, b.Digits);
    }

    public bool Equals(DynamoNumber other) => CompareTo(other) == 0;

    public override bool Equals(object? obj) => obj is DynamoNumber other && Equals(other);

    public override int GetHashCode() =>
        IsZero ? 0 : unchecked((StringComparer.Ordinal.GetHashCode(Digits) * 31 + Point) * 2 + (Negative ? 1 : 0));

    public static bool operator ==(DynamoNumber left, DynamoNumber right) => left.Equals(right);

    public static bool operator !=(DynamoNumber left, DynamoNumber right) => !left.Equals(right);

    public static bool operator <(DynamoNumber left, DynamoNumber right) => left.CompareTo(right) < 0;

    public static bool operator >(DynamoNumber left, DynamoNumber right) => left.CompareTo(right) > 0;

    public static bool operator <=(DynamoNumber left, DynamoNumber right) => left.CompareTo(right) <= 0;

    public static bool operator >=(DynamoNumber left, DynamoNumber right) => left.CompareTo(right) >= 0;
}
