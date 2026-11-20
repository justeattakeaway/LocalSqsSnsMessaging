#pragma warning disable CA1308 // S3 writes metadata names and checksum algorithms in lower case.
using System.Security.Cryptography;

namespace InMemoryAws.S3;

/// <summary>
/// The checksums S3 computes and checks (<c>x-amz-checksum-*</c>): CRC32, CRC32C and CRC64NVME, and
/// the SHA-1 and SHA-256 digests, each sent as the base64 of its big-endian bytes; and MD5, which
/// S3 uses for ETags and <c>Content-MD5</c>.
/// </summary>
internal static class S3Checksums
{
    /// <summary>The algorithms S3 accepts, as <c>x-amz-checksum-algorithm</c> names them.</summary>
    public static readonly string[] Algorithms = ["CRC32", "CRC32C", "CRC64NVME", "SHA1", "SHA256"];

    private static readonly uint[] Crc32CTable = BuildCrc32Table(0x82F63B78u);
    private static readonly ulong[] Crc64NvmeTable = BuildCrc64Table(0x9A6C9329AC4BC9B5ul);

    /// <summary>The header that carries an algorithm's checksum: <c>x-amz-checksum-crc32</c>.</summary>
    public static string HeaderName(string algorithm) => "x-amz-checksum-" + algorithm.ToLowerInvariant();

    /// <summary>Whether <paramref name="algorithm"/> is one S3 accepts (case-insensitive).</summary>
    public static string? Normalize(string? algorithm)
    {
        if (algorithm is null)
        {
            return null;
        }

        foreach (var known in Algorithms)
        {
            if (string.Equals(known, algorithm, StringComparison.OrdinalIgnoreCase))
            {
                return known;
            }
        }

        return null;
    }

    /// <summary>Whether the algorithm is a CRC, whose checksums of parts combine into the whole object's.</summary>
    public static bool IsCrc(string algorithm) => algorithm is "CRC32" or "CRC32C" or "CRC64NVME";

    /// <summary>The raw checksum bytes of <paramref name="data"/>.</summary>
    public static byte[] ComputeBytes(string algorithm, ReadOnlySpan<byte> data)
    {
        switch (algorithm)
        {
            case "CRC32":
                return BigEndian(Http.Crc32.Compute(data));
            case "CRC32C":
                return BigEndian(Crc32C(data));
            case "CRC64NVME":
                return BigEndian(Crc64Nvme(data));
            case "SHA1":
#pragma warning disable CA5350 // S3 offers SHA-1 checksums; this is not used for security.
                return SHA1.HashData(data.ToArray());
#pragma warning restore CA5350

            case "SHA256":
                return SHA256.HashData(data.ToArray());

            default:
                throw new ArgumentOutOfRangeException(nameof(algorithm), algorithm, "Unknown checksum algorithm.");
        }
    }

    /// <summary>The checksum of <paramref name="data"/> as S3 sends it: base64.</summary>
    public static string Compute(string algorithm, ReadOnlySpan<byte> data) => Convert.ToBase64String(ComputeBytes(algorithm, data));

    /// <summary>
    /// A multipart object's composite checksum: the checksum of its parts' checksums, concatenated,
    /// then <c>-</c> and the number of parts.
    /// </summary>
    public static string Composite(string algorithm, IReadOnlyList<string> partChecksums)
    {
        using var all = new MemoryStream();
        foreach (var part in partChecksums)
        {
            var bytes = Convert.FromBase64String(part);
            all.Write(bytes, 0, bytes.Length);
        }

        return Compute(algorithm, all.ToArray()) + "-" + partChecksums.Count.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>The MD5 digest (ETags and <c>Content-MD5</c>).</summary>
    public static byte[] Md5(ReadOnlySpan<byte> data)
    {
#pragma warning disable CA5351 // S3's ETags and Content-MD5 are MD5; this is not used for security.
        return MD5.HashData(data.ToArray());
#pragma warning restore CA5351
    }

    /// <summary>An object's ETag: its MD5 in lower-case hex, quoted.</summary>
    public static string ETag(ReadOnlySpan<byte> data) => "\"" + Hex(Md5(data)) + "\"";

    /// <summary>A multipart object's ETag: the MD5 of its parts' MD5s, then <c>-</c> and the number of parts, quoted.</summary>
    public static string MultipartETag(IReadOnlyList<string> partETags)
    {
        using var all = new MemoryStream();
        foreach (var etag in partETags)
        {
            var bytes = FromHex(etag.Trim('"'));
            all.Write(bytes, 0, bytes.Length);
        }

        return "\"" + Hex(Md5(all.ToArray())) + "-" + partETags.Count.ToString(System.Globalization.CultureInfo.InvariantCulture) + "\"";
    }

    public static string Hex(byte[] bytes)
    {
        var chars = new char[bytes.Length * 2];
        for (var i = 0; i < bytes.Length; i++)
        {
            chars[i * 2] = "0123456789abcdef"[bytes[i] >> 4];
            chars[(i * 2) + 1] = "0123456789abcdef"[bytes[i] & 0xF];
        }

        return new string(chars);
    }

    private static byte[] FromHex(string hex)
    {
        if (hex.Length % 2 != 0)
        {
            return [];
        }

        var bytes = new byte[hex.Length / 2];
        for (var i = 0; i < bytes.Length; i++)
        {
            if (!byte.TryParse(hex.AsSpan(i * 2, 2), System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out bytes[i]))
            {
                return [];
            }
        }

        return bytes;
    }

    private static uint Crc32C(ReadOnlySpan<byte> data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in data)
        {
            crc = Crc32CTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        }

        return ~crc;
    }

    private static ulong Crc64Nvme(ReadOnlySpan<byte> data)
    {
        var crc = ulong.MaxValue;
        foreach (var b in data)
        {
            crc = Crc64NvmeTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        }

        return ~crc;
    }

    private static uint[] BuildCrc32Table(uint polynomial)
    {
        var table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            var value = i;
            for (var bit = 0; bit < 8; bit++)
            {
                value = (value & 1) != 0 ? polynomial ^ (value >> 1) : value >> 1;
            }

            table[i] = value;
        }

        return table;
    }

    private static ulong[] BuildCrc64Table(ulong polynomial)
    {
        var table = new ulong[256];
        for (ulong i = 0; i < 256; i++)
        {
            var value = i;
            for (var bit = 0; bit < 8; bit++)
            {
                value = (value & 1) != 0 ? polynomial ^ (value >> 1) : value >> 1;
            }

            table[i] = value;
        }

        return table;
    }

    private static byte[] BigEndian(uint value) =>
        [(byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value];

    private static byte[] BigEndian(ulong value) =>
        [(byte)(value >> 56), (byte)(value >> 48), (byte)(value >> 40), (byte)(value >> 32), (byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value];
}
