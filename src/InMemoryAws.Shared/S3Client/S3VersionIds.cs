using System.Security.Cryptography;

namespace InMemoryAws.S3;

/// <summary>
/// The version ids the cloud mints: 32 URL-safe characters, as S3's are, carrying a check value so
/// that a structurally invalid id (one character altered, say) is told apart from a well-formed id
/// of a version that does not exist, as S3 tells them apart (<c>InvalidArgument</c> against
/// <c>NoSuchVersion</c>).
/// </summary>
internal static class S3VersionIds
{
    private const int RandomBytes = 20;
    private const int CheckBytes = 4;

    /// <summary>A new version id: 20 random bytes and a 4-byte check value over them, base64url-encoded.</summary>
    public static string New(RandomNumberGenerator random)
    {
        var buffer = new byte[RandomBytes + CheckBytes];
        random.GetBytes(buffer, 0, RandomBytes);
        WriteCheck(buffer, buffer);
        return Convert.ToBase64String(buffer).Replace('+', '.').Replace('/', '_');
    }

    /// <summary>
    /// False when <paramref name="versionId"/> has the shape of a minted id (32 characters of its
    /// alphabet) but its check value does not hold: an id S3 could never have minted.
    /// </summary>
    public static bool IsIntact(string versionId)
    {
        if (versionId.Length != 32)
        {
            return true;
        }

        byte[] decoded;
        try
        {
            decoded = Convert.FromBase64String(versionId.Replace('.', '+').Replace('_', '/'));
        }
        catch (FormatException)
        {
            return true;
        }

        var expected = new byte[RandomBytes + CheckBytes];
        WriteCheck(decoded, expected);
        for (var i = RandomBytes; i < expected.Length; i++)
        {
            if (expected[i] != decoded[i])
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Whether <paramref name="versionId"/> is an id the cloud could have minted: 32 characters that
    /// decode to 24 bytes whose check value holds. Any other id names no version that ever existed.
    /// </summary>
    public static bool CouldBeMinted(string versionId)
    {
        if (versionId.Length != 32 || versionId.Any(c => !(char.IsLetterOrDigit(c) && c < 128) && c is not ('.' or '_')))
        {
            return false;
        }

        return IsIntact(versionId);
    }

    /// <summary>Writes the FNV-1a hash of the first 20 bytes of <paramref name="source"/> into the last 4 of <paramref name="target"/>.</summary>
    private static void WriteCheck(byte[] source, byte[] target)
    {
        var hash = 2166136261u;
        for (var i = 0; i < RandomBytes; i++)
        {
            hash = unchecked((hash ^ source[i]) * 16777619u);
        }

        for (var i = 0; i < CheckBytes; i++)
        {
            target[RandomBytes + i] = (byte)(hash >> (8 * i));
        }
    }
}
