using System.Globalization;
using System.Text.Json;

namespace LocalSqsSnsMessaging.Http.Serialization;

/// <summary>
/// Timestamps in the AWS JSON protocol: epoch seconds as a JSON number (<c>1731590400.123</c>),
/// which is what the AWS SDKs send and what AWS answers with.
/// </summary>
internal static class AwsJsonTimestamp
{
    /// <summary>
    /// Reads a timestamp member as UTC: epoch seconds (a number, or a string of digits), or an
    /// ISO 8601 string. Returns false for anything else.
    /// </summary>
    public static bool TryRead(JsonElement element, out DateTime value)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Number when element.TryGetDecimal(out var seconds):
                return TryFromEpochSeconds(seconds, out value);
            case JsonValueKind.String:
                var text = element.GetString();
                if (decimal.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
                {
                    return TryFromEpochSeconds(parsed, out value);
                }

                if (DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var offset))
                {
                    value = offset.UtcDateTime;
                    return true;
                }

                break;
        }

        value = default;
        return false;
    }

    /// <summary>Writes <paramref name="value"/> as epoch seconds, to the millisecond.</summary>
    public static void Write(Utf8JsonWriter writer, string propertyName, DateTime value)
    {
        var utc = value.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(value, DateTimeKind.Utc) : value.ToUniversalTime();
        var milliseconds = new DateTimeOffset(utc).ToUnixTimeMilliseconds();
        writer.WriteNumber(propertyName, milliseconds / 1000m);
    }

    private static bool TryFromEpochSeconds(decimal seconds, out DateTime value)
    {
        const decimal MaxSeconds = 253402300799m; // 9999-12-31T23:59:59Z
        if (seconds < 0 || seconds > MaxSeconds)
        {
            value = default;
            return false;
        }

        value = DateTimeOffset.FromUnixTimeMilliseconds((long)decimal.Round(seconds * 1000m)).UtcDateTime;
        return true;
    }
}
