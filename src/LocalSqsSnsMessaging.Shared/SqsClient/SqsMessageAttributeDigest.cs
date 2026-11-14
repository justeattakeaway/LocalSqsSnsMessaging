using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using LocalSqsSnsMessaging.Sqs.Model;

namespace LocalSqsSnsMessaging;

/// <summary>
/// The MD5 digests SQS reports over message attributes (<c>MD5OfMessageAttributes</c> and
/// <c>MD5OfMessageSystemAttributes</c>), computed over SQS's documented canonical encoding so SDK
/// checksum validation accepts them.
/// </summary>
/// <remarks>
/// For each attribute, in ordinal order of name: the name, then the data type, each as a 4-byte
/// big-endian length followed by its UTF-8 bytes; then a transport-type byte (1 for String and
/// Number, 2 for Binary); then the value as a 4-byte big-endian length followed by its bytes.
/// </remarks>
internal static class SqsMessageAttributeDigest
{
    private const byte StringTransportType = 1;
    private const byte BinaryTransportType = 2;

    /// <summary>The digest of <paramref name="attributes"/>, or <see langword="null"/> when there are none.</summary>
    public static string? Compute(Dictionary<string, MessageAttributeValue>? attributes)
    {
        if (attributes is not { Count: > 0 })
        {
            return null;
        }

        using var buffer = new MemoryStream();
        foreach (var (name, value) in attributes.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            Append(buffer, name, value.DataType, value.StringValue, value.BinaryValue);
        }

        return Hash(buffer);
    }

    /// <summary>The digest of <paramref name="attributes"/>, or <see langword="null"/> when there are none.</summary>
    public static string? Compute(Dictionary<string, MessageSystemAttributeValue>? attributes)
    {
        if (attributes is not { Count: > 0 })
        {
            return null;
        }

        using var buffer = new MemoryStream();
        foreach (var (name, value) in attributes.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            Append(buffer, name, value.DataType, value.StringValue, value.BinaryValue);
        }

        return Hash(buffer);
    }

    private static void Append(MemoryStream buffer, string name, string? dataType, string? stringValue, MemoryStream? binaryValue)
    {
        dataType ??= "String";
        WriteLengthPrefixed(buffer, Encoding.UTF8.GetBytes(name));
        WriteLengthPrefixed(buffer, Encoding.UTF8.GetBytes(dataType));

        if (dataType.StartsWith("Binary", StringComparison.Ordinal))
        {
            buffer.WriteByte(BinaryTransportType);
            WriteLengthPrefixed(buffer, binaryValue?.ToArray() ?? []);
        }
        else
        {
            buffer.WriteByte(StringTransportType);
            WriteLengthPrefixed(buffer, Encoding.UTF8.GetBytes(stringValue ?? string.Empty));
        }
    }

    private static void WriteLengthPrefixed(MemoryStream buffer, byte[] bytes)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, bytes.Length);
        buffer.Write(length);
        buffer.Write(bytes);
    }

    private static string Hash(MemoryStream buffer)
    {
#pragma warning disable CA5351 // MD5 is what the SQS wire protocol specifies.
        var hash = MD5.HashData(buffer.GetBuffer().AsSpan(0, (int)buffer.Length));
#pragma warning restore CA5351
#pragma warning disable CA1308 // SQS reports digests in lower case.
        return Convert.ToHexString(hash).ToLowerInvariant();
#pragma warning restore CA1308
    }
}
