// UTF-8 optimized query string parser for AWS Query protocol
// Avoids string materialization required by HttpUtility.ParseQueryString

using System.Buffers;
using System.Text;

namespace LocalSqsSnsMessaging.Http;

/// <summary>
/// Optimized query string parser that works directly with UTF-8 bytes.
/// This avoids the string materialization required by HttpUtility.ParseQueryString.
///
/// Suitable for AWS Query protocol which has:
/// - Simple key=value pairs
/// - &amp; separator
/// - URL encoding for special characters
/// </summary>
internal static class QueryStringParser
{
    /// <summary>
    /// Parse a query string from UTF-8 bytes into a dictionary of key-value pairs.
    /// </summary>
    /// <param name="utf8Bytes">The UTF-8 encoded query string bytes.</param>
    /// <returns>Dictionary of decoded key-value pairs.</returns>
    public static Dictionary<string, string> Parse(ReadOnlySpan<byte> utf8Bytes)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);

        // Split on '&' character
        var remaining = utf8Bytes;
        while (remaining.Length > 0)
        {
            var ampersandIndex = remaining.IndexOf((byte)'&');
            var pair = ampersandIndex >= 0
                ? remaining.Slice(0, ampersandIndex)
                : remaining;

            // Find the '=' separator
            var equalsIndex = pair.IndexOf((byte)'=');
            if (equalsIndex >= 0)
            {
                var keyBytes = pair.Slice(0, equalsIndex);
                var valueBytes = pair.Slice(equalsIndex + 1);

                // Decode key and value
                var key = UrlDecode(keyBytes);
                var value = UrlDecode(valueBytes);

                result[key] = value;
            }

            // Move to next pair
            if (ampersandIndex >= 0)
                remaining = remaining.Slice(ampersandIndex + 1);
            else
                break;
        }

        return result;
    }

    /// <summary>
    /// URL decode from UTF-8 bytes to string.
    /// Handles:
    /// - %XX hex encoding
    /// - + as space
    /// </summary>
    private static string UrlDecode(ReadOnlySpan<byte> encoded)
    {
        // Fast path: no encoding needed
        if (encoded.IndexOf((byte)'%') < 0 && encoded.IndexOf((byte)'+') < 0)
        {
            return Encoding.UTF8.GetString(encoded);
        }

        // Slow path: decode
        Span<byte> decoded = stackalloc byte[encoded.Length]; // Max size
        int writePos = 0;

        for (int i = 0; i < encoded.Length; i++)
        {
            if (encoded[i] == (byte)'%' && i + 2 < encoded.Length)
            {
                // Decode %XX
                var hex = encoded.Slice(i + 1, 2);
                if (TryParseHexByte(hex, out byte decodedByte))
                {
                    decoded[writePos++] = decodedByte;
                    i += 2;
                    continue;
                }
            }
            else if (encoded[i] == (byte)'+')
            {
                decoded[writePos++] = (byte)' ';
                continue;
            }

            decoded[writePos++] = encoded[i];
        }

        return Encoding.UTF8.GetString(decoded.Slice(0, writePos));
    }

    private static bool TryParseHexByte(ReadOnlySpan<byte> hex, out byte result)
    {
        result = 0;
        if (hex.Length != 2)
            return false;

        if (!TryParseHexChar(hex[0], out byte high) ||
            !TryParseHexChar(hex[1], out byte low))
            return false;

        result = (byte)((high << 4) | low);
        return true;
    }

    private static bool TryParseHexChar(byte c, out byte value)
    {
        if (c >= '0' && c <= '9')
        {
            value = (byte)(c - '0');
            return true;
        }
        if (c >= 'A' && c <= 'F')
        {
            value = (byte)(c - 'A' + 10);
            return true;
        }
        if (c >= 'a' && c <= 'f')
        {
            value = (byte)(c - 'a' + 10);
            return true;
        }
        value = 0;
        return false;
    }
}
