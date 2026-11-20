using System.Globalization;
using System.Xml.Linq;
using InMemoryAws.S3;

namespace InMemoryAws.Http;

/// <summary>
/// How S3's REST-XML protocol writes values in headers, query parameters and XML: the generated
/// dispatcher and serializers call these.
/// </summary>
internal static class S3Wire
{
    /// <summary>A header or query value as an integer; a value that isn't one is <c>InvalidArgument</c>.</summary>
    public static int? ParseInt(string? value, string name)
    {
        if (value is null)
        {
            return null;
        }

        return int.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var number)
            ? number
            : throw S3Errors.InvalidArgument($"Provided {name} not an integer or within integer range", name, value);
    }

    public static long? ParseLong(string? value, string name)
    {
        if (value is null)
        {
            return null;
        }

        return long.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var number)
            ? number
            : throw S3Errors.InvalidArgument($"Provided {name} not an integer or within integer range", name, value);
    }

    public static bool? ParseBool(string? value, string name)
    {
        if (value is null)
        {
            return null;
        }

        if (string.Equals(value, "true", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (string.Equals(value, "false", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        throw S3Errors.InvalidArgument($"Invalid value for {name}: {value}", name, value);
    }

    /// <summary>
    /// A header or query timestamp (RFC 822 dates in headers, ISO 8601 in the query, unless the
    /// model says otherwise); a value that parses in neither form is ignored, as S3 ignores an
    /// unparseable conditional date.
    /// </summary>
    public static DateTime? ParseTimestamp(string? value, string format)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var parsed))
        {
            return parsed.UtcDateTime;
        }

        if (format == "unixTimestamp" && double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds))
        {
            return DateTimeOffset.FromUnixTimeMilliseconds((long)(seconds * 1000)).UtcDateTime;
        }

        return null;
    }

    /// <summary>A comma-separated header value as a list (<c>x-amz-object-attributes</c>).</summary>
    public static List<string>? ParseList(string? value) =>
        value is null
            ? null
            : value.Split(',').Select(v => v.Trim()).Where(v => v.Length > 0).ToList();

    public static string FormatNumber(long value) => value.ToString(CultureInfo.InvariantCulture);

    public static string FormatNumber(int value) => value.ToString(CultureInfo.InvariantCulture);

    public static string FormatNumber(double value) => value.ToString("R", CultureInfo.InvariantCulture);

    /// <summary>
    /// A timestamp as S3 writes it: <c>Wed, 12 Oct 2009 17:50:00 GMT</c> in headers (rfc822),
    /// <c>2009-10-12T17:50:30.000Z</c> in XML (iso8601).
    /// </summary>
    public static string FormatTimestamp(DateTime value, string format)
    {
        var utc = value.Kind == DateTimeKind.Local ? value.ToUniversalTime() : value;
        return format switch
        {
            "rfc822" => utc.ToString("ddd, dd MMM yyyy HH:mm:ss 'GMT'", CultureInfo.InvariantCulture),
            "unixTimestamp" => (new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc)).ToUnixTimeMilliseconds() / 1000.0).ToString("0.###", CultureInfo.InvariantCulture),
            _ => utc.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture),
        };
    }
}

/// <summary>Reads the values of an XML request body; a value of the wrong form is <c>MalformedXML</c>.</summary>
internal static class S3Xml
{
    public static string String(XElement element) => element.Value;

    public static int Int(XElement element) =>
        int.TryParse(element.Value.Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var value) ? value : throw S3Errors.MalformedXml();

    public static long Long(XElement element) =>
        long.TryParse(element.Value.Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var value) ? value : throw S3Errors.MalformedXml();

    public static double Double(XElement element) =>
        double.TryParse(element.Value.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : throw S3Errors.MalformedXml();

    public static bool Bool(XElement element) => element.Value.Trim() switch
    {
        "true" => true,
        "false" => false,
        _ => throw S3Errors.MalformedXml(),
    };

    public static DateTime Timestamp(XElement element) =>
        DateTimeOffset.TryParse(element.Value.Trim(), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var value)
            ? value.UtcDateTime
            : throw S3Errors.MalformedXml();

    public static byte[] Blob(XElement element)
    {
        try
        {
            return Convert.FromBase64String(element.Value.Trim());
        }
        catch (FormatException)
        {
            throw S3Errors.MalformedXml();
        }
    }

    /// <summary>An attribute by local name (<c>xsi:type</c> is <c>type</c>).</summary>
    public static string? Attribute(XElement element, string localName)
    {
        foreach (var attribute in element.Attributes())
        {
            if (!attribute.IsNamespaceDeclaration && attribute.Name.LocalName == localName)
            {
                return attribute.Value;
            }
        }

        return null;
    }

    /// <summary>A list item must be named as the schema names it.</summary>
    public static void ExpectName(XElement element, string localName)
    {
        if (element.Name.LocalName != localName)
        {
            throw S3Errors.MalformedXml();
        }
    }

    /// <summary>An element the schema doesn't have where it appears.</summary>
    public static AwsServiceException UnexpectedElement(XElement element)
    {
        _ = element;
        return S3Errors.MalformedXml();
    }
}
