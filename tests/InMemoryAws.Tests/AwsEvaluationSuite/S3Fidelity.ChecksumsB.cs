using Amazon.S3;
using Amazon.S3.Model;

namespace InMemoryAws.Tests.AwsEvaluationSuite;

/// <summary>boto3's views of the checksum members the multipart and copy ports compare.</summary>
public static partial class S3Fidelity
{
    /// <summary>
    /// boto3's <c>attrs["Checksum"]</c> (GetObjectAttributes): a dict of the members present, or
    /// <c>None</c>, for <see cref="Py.Equal"/> and <see cref="Py.Repr"/>.
    /// </summary>
    public static Dictionary<string, object?>? ChecksumDict(Checksum? checksum)
    {
        if (checksum is null)
        {
            return null;
        }

        var members = new Dictionary<string, object?>(StringComparer.Ordinal);
        void Add(string name, string? value)
        {
            if (value is not null)
            {
                members[name] = value;
            }
        }

        Add("ChecksumCRC32", checksum.ChecksumCRC32);
        Add("ChecksumCRC32C", checksum.ChecksumCRC32C);
        Add("ChecksumCRC64NVME", checksum.ChecksumCRC64NVME);
        Add("ChecksumSHA1", checksum.ChecksumSHA1);
        Add("ChecksumSHA256", checksum.ChecksumSHA256);
        Add("ChecksumType", checksum.ChecksumType?.Value);
        return members;
    }

    /// <summary>
    /// boto3's dict of one <c>ObjectParts.Parts</c> entry (GetObjectAttributes): its number, size and
    /// whichever checksums it carries.
    /// </summary>
    public static Dictionary<string, object?> PartDict(ObjectPart part)
    {
        ArgumentNullException.ThrowIfNull(part);
        var members = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["PartNumber"] = part.PartNumber,
        };
        if (part.Size is { } size)
        {
            members["Size"] = size;
        }

        void Add(string name, string? value)
        {
            if (value is not null)
            {
                members[name] = value;
            }
        }

        Add("ChecksumCRC32", part.ChecksumCRC32);
        Add("ChecksumCRC32C", part.ChecksumCRC32C);
        Add("ChecksumCRC64NVME", part.ChecksumCRC64NVME);
        Add("ChecksumSHA1", part.ChecksumSHA1);
        Add("ChecksumSHA256", part.ChecksumSHA256);
        return members;
    }
}
