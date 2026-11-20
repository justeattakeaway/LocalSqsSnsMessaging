#pragma warning disable SA1402 // One interface and the generated models that implement it.
#pragma warning disable SA1649

namespace InMemoryAws.S3.Model;

/// <summary>
/// The checksum members (<c>ChecksumCRC32</c>, ...) every model that carries an object's or a
/// part's checksum has, so one helper reads and writes them whichever model it is.
/// </summary>
internal interface IS3ChecksumMembers
{
    string? ChecksumCRC32 { get; set; }

    string? ChecksumCRC32C { get; set; }

    string? ChecksumCRC64NVME { get; set; }

    string? ChecksumSHA1 { get; set; }

    string? ChecksumSHA256 { get; set; }
}

/// <summary>The headers GetObject and HeadObject both answer about an object.</summary>
internal interface IS3ObjectHeaders : IS3ChecksumMembers
{
    bool? DeleteMarker { get; set; }

    string? AcceptRanges { get; set; }

    string? Expiration { get; set; }

    string? Restore { get; set; }

    DateTime? LastModified { get; set; }

    long? ContentLength { get; set; }

    string? ETag { get; set; }

    string? ChecksumType { get; set; }

    int? MissingMeta { get; set; }

    string? VersionId { get; set; }

    string? CacheControl { get; set; }

    string? ContentDisposition { get; set; }

    string? ContentEncoding { get; set; }

    string? ContentLanguage { get; set; }

    string? ContentRange { get; set; }

    string? ContentType { get; set; }

    string? Expires { get; set; }

    string? WebsiteRedirectLocation { get; set; }

    string? ServerSideEncryption { get; set; }

    Dictionary<string, string>? Metadata { get; set; }

    string? SSECustomerAlgorithm { get; set; }

    string? SSECustomerKeyMD5 { get; set; }

    string? SSEKMSKeyId { get; set; }

    bool? BucketKeyEnabled { get; set; }

    string? StorageClass { get; set; }

    string? ReplicationStatus { get; set; }

    int? PartsCount { get; set; }

    int? TagCount { get; set; }

    string? ObjectLockMode { get; set; }

    DateTime? ObjectLockRetainUntilDate { get; set; }

    string? ObjectLockLegalHoldStatus { get; set; }
}

/// <summary>The conditions and overrides GetObject and HeadObject both take.</summary>
internal interface IS3ObjectRead
{
    string? Bucket { get; set; }

    string? Key { get; set; }

    string? VersionId { get; set; }

    string? IfMatch { get; set; }

    DateTime? IfModifiedSince { get; set; }

    string? IfNoneMatch { get; set; }

    DateTime? IfUnmodifiedSince { get; set; }

    string? Range { get; set; }

    int? PartNumber { get; set; }

    string? ChecksumMode { get; set; }

    string? ResponseCacheControl { get; set; }

    string? ResponseContentDisposition { get; set; }

    string? ResponseContentEncoding { get; set; }

    string? ResponseContentLanguage { get; set; }

    string? ResponseContentType { get; set; }

    DateTime? ResponseExpires { get; set; }

    string? SSECustomerAlgorithm { get; set; }

    string? SSECustomerKey { get; set; }

    string? SSECustomerKeyMD5 { get; set; }
}

internal sealed partial class Checksum : IS3ChecksumMembers;

internal sealed partial class CompleteMultipartUploadOutput : IS3ChecksumMembers;

internal sealed partial class CompleteMultipartUploadRequest : IS3ChecksumMembers;

internal sealed partial class CompletedPart : IS3ChecksumMembers;

internal sealed partial class CopyObjectResult : IS3ChecksumMembers;

internal sealed partial class CopyPartResult : IS3ChecksumMembers;

internal sealed partial class ObjectPart : IS3ChecksumMembers;

internal sealed partial class Part : IS3ChecksumMembers;

internal sealed partial class PutObjectOutput : IS3ChecksumMembers;

internal sealed partial class PutObjectRequest : IS3ChecksumMembers;

internal sealed partial class UploadPartOutput : IS3ChecksumMembers;

internal sealed partial class UploadPartRequest : IS3ChecksumMembers;

internal sealed partial class GetObjectOutput : IS3ObjectHeaders;

internal sealed partial class HeadObjectOutput : IS3ObjectHeaders;

internal sealed partial class GetObjectRequest : IS3ObjectRead;

internal sealed partial class HeadObjectRequest : IS3ObjectRead;
