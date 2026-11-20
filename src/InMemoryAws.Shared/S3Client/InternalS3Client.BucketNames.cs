using InMemoryAws.S3.Model;

namespace InMemoryAws.S3;

/// <summary>The bucket names S3 reserves for its own features, which CreateBucket refuses as a matter of access, not of shape.</summary>
internal sealed partial class InternalS3Client
{
    /// <summary>Prefixes S3 keeps for itself.</summary>
    private static readonly string[] AccessReservedPrefixes = ["sthree-", "amzn-s3-demo-"];

    /// <summary>Suffixes of access point, Object Lambda access point and Multi-Region Access Point aliases.</summary>
    private static readonly string[] AccessReservedSuffixes = ["-s3alias", "--ol-s3", ".mrap"];

    /// <summary>
    /// Refuses a name that is otherwise well formed but starts with a prefix or ends with a suffix S3
    /// reserves (<c>AccessDenied</c>). A name ending <c>--x-s3</c> names a directory bucket, so a
    /// general purpose bucket's configuration sent with it doesn't fit the schema (<c>MalformedXML</c>).
    /// A name that is not well formed is left to <see cref="S3BucketNames.IsValid"/>.
    /// </summary>
    private static void CheckReservedBucketName(string name, CreateBucketRequest request)
    {
        foreach (var prefix in AccessReservedPrefixes)
        {
            if (name.StartsWith(prefix, StringComparison.Ordinal) && WellFormedApartFrom(name.Remove(0, prefix.Length)))
            {
                throw S3Errors.AccessDenied("The requested bucket name starts with a reserved prefix");
            }
        }

        foreach (var suffix in AccessReservedSuffixes)
        {
            if (name.EndsWith(suffix, StringComparison.Ordinal) && WellFormedApartFrom(name.Remove(name.Length - suffix.Length)))
            {
                throw S3Errors.AccessDenied("The requested bucket name ends with a reserved suffix");
            }
        }

        if (name.EndsWith("--x-s3", StringComparison.Ordinal) && request.CreateBucketConfiguration?.Bucket is null
            && WellFormedApartFrom(name.Remove(name.Length - "--x-s3".Length)))
        {
            throw S3Errors.MalformedXml();
        }

        static bool WellFormedApartFrom(string rest) => S3BucketNames.IsValid(rest.PadRight(3, '0'));
    }
}
