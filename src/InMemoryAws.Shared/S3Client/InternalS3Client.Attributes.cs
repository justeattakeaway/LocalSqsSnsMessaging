using System.Globalization;
using InMemoryAws.S3.Model;

namespace InMemoryAws.S3;

internal sealed partial class InternalS3Client
{
    /// <summary>The attribute names GetObjectAttributes takes (<c>x-amz-object-attributes</c>).</summary>
    private static readonly HashSet<string> ObjectAttributeNames = new(StringComparer.Ordinal)
    {
        "ETag", "Checksum", "ObjectParts", "StorageClass", "ObjectSize",
    };

    /// <summary>
    /// The attributes of an object (or one version of it) the request names: its ETag (unquoted), its
    /// checksum (a composite one without its <c>-N</c> suffix), its parts (paged as ListParts pages
    /// them; each part's size and checksum only for a composite checksum, which keeps them), its
    /// storage class and its size. An unknown attribute name is <c>InvalidArgument</c>.
    /// </summary>
    public override Task<GetObjectAttributesOutput> GetObjectAttributesAsync(GetObjectAttributesRequest request, CancellationToken cancellationToken)
    {
        var attributes = request.ObjectAttributes;
        if (attributes is null || attributes.Count == 0)
        {
            throw S3Errors.InvalidArgument("Empty attribute specified.", "x-amz-object-attributes", string.Empty);
        }

        foreach (var attribute in attributes)
        {
            if (!ObjectAttributeNames.Contains(attribute))
            {
                throw S3Errors.InvalidArgument("Invalid attribute name specified.", "x-amz-object-attributes", attribute);
            }
        }

        var maxParts = request.MaxParts ?? 1000;
        if (maxParts < 0)
        {
            throw S3Errors.InvalidArgument("Argument max-parts must be an integer between 0 and 2147483647", "max-parts", maxParts.ToString(CultureInfo.InvariantCulture));
        }

        maxParts = Math.Min(maxParts, 1000);
        lock (Store.Gate)
        {
            var bucket = GetBucket(request.Bucket);
            var key = CheckKey(request.Key);
            var version = AttributesVersion(bucket, key, request.VersionId);
            CheckSseCustomerKeyForAttributes(version, request);
            var output = new GetObjectAttributesOutput
            {
                LastModified = version.LastModified,
                VersionId = VersionIdHeader(bucket, version),
            };
            if (attributes.Contains("ETag"))
            {
                output.ETag = version.ETag.Trim('"');
            }

            if (attributes.Contains("Checksum") && version.Checksum is { } checksum)
            {
                var value = version.ChecksumType == "COMPOSITE" && checksum.LastIndexOf('-') is var dash and > 0 ? checksum.Substring(0, dash) : checksum;
                var reported = new Checksum { ChecksumType = version.ChecksumType };
                SetChecksum(reported, version.ChecksumAlgorithm, value);
                output.Checksum = reported;
            }

            if (attributes.Contains("ObjectParts") && version.Parts is { } parts)
            {
                output.ObjectParts = DescribeParts(version, parts, request.PartNumberMarker ?? 0, maxParts);
            }

            if (attributes.Contains("StorageClass"))
            {
                output.StorageClass = version.StorageClass;
            }

            if (attributes.Contains("ObjectSize"))
            {
                output.ObjectSize = version.Size;
            }

            return Task.FromResult(output);
        }
    }

    /// <summary>
    /// A multipart object's parts: their count, and for a composite checksum (which is built from
    /// each part's own) a page of the parts with their sizes and checksums.
    /// </summary>
    private static GetObjectAttributesParts DescribeParts(S3ObjectVersion version, List<S3StoredPart> parts, int marker, int maxParts)
    {
        var described = new GetObjectAttributesParts { TotalPartsCount = parts.Count };
        if (version.ChecksumType != "COMPOSITE")
        {
            return described;
        }

        var remaining = parts.Where(p => p.PartNumber > marker).ToList();
        var page = remaining.Take(maxParts).ToList();
        described.PartNumberMarker = marker;
        described.MaxParts = maxParts;
        described.IsTruncated = remaining.Count > page.Count;
        described.NextPartNumberMarker = page.Count > 0 ? page[^1].PartNumber : marker;
        described.Parts = [];
        foreach (var part in page)
        {
            var listed = new ObjectPart { PartNumber = part.PartNumber, Size = part.Size };
            SetChecksum(listed, version.ChecksumAlgorithm, part.Checksum);
            described.Parts.Add(listed);
        }

        return described;
    }

    /// <summary>
    /// The version GetObjectAttributes describes: a delete marker is <c>NoSuchKey</c> when current
    /// (with <c>x-amz-delete-marker</c>), and <c>MethodNotAllowed</c> when named by version id.
    /// </summary>
    private S3ObjectVersion AttributesVersion(S3Bucket bucket, string key, string? versionId)
    {
        if (versionId is not null)
        {
            CheckVersionIdFormat(versionId);
            var version = bucket.Version(key, versionId) ?? throw S3Errors.NoSuchVersion(key, versionId);
            if (version.IsDeleteMarker)
            {
                throw S3Errors.MethodNotAllowed(Request?.Method ?? "GET", "DeleteMarker")
                    .WithHeader("x-amz-delete-marker", "true")
                    .WithHeader("x-amz-version-id", version.VersionId);
            }

            return version;
        }

        var current = bucket.Current(key) ?? throw S3Errors.NoSuchKey(key);
        if (current.IsDeleteMarker)
        {
            throw S3Errors.NoSuchKey(key)
                .WithHeader("x-amz-delete-marker", "true")
                .WithHeader("x-amz-version-id", current.VersionId);
        }

        return current;
    }
}
