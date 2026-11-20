using InMemoryAws.S3.Model;

namespace InMemoryAws.S3;

/// <summary>
/// The cloud's S3: every bucket, whatever its region, because bucket names are global. Every S3
/// operation holds <see cref="Gate"/>, so an operation sees and leaves the store whole.
/// </summary>
internal sealed class S3Store
{
    public object Gate { get; } = new();

    /// <summary>The buckets by name.</summary>
    public Dictionary<string, S3Bucket> Buckets { get; } = new(StringComparer.Ordinal);
}

/// <summary>
/// One bucket: its settings, its objects (each key's versions, newest first) and its multipart
/// uploads. Configurations S3 keeps as sent are kept as the request's model.
/// </summary>
internal sealed partial class S3Bucket
{
    public S3Bucket(string name, string region, string ownerId, DateTime created)
    {
        Name = name;
        Region = region;
        OwnerId = ownerId;
        Created = created;
    }

    public string Name { get; }

    /// <summary>The region the bucket was created in (its <c>LocationConstraint</c>, or <c>us-east-1</c>).</summary>
    public string Region { get; }

    /// <summary>The canonical user id of the bucket's owner.</summary>
    public string OwnerId { get; }

    public DateTime Created { get; }

    /// <summary>The bucket's ARN: <c>arn:aws:s3:::name</c>.</summary>
    public string Arn => "arn:aws:s3:::" + Name;

    /// <summary><c>Enabled</c> or <c>Suspended</c> once versioning has been set; null for a bucket never versioned.</summary>
    public string? Versioning { get; set; }

    /// <summary><c>Enabled</c> or <c>Disabled</c> once set with versioning.</summary>
    public string? MfaDelete { get; set; }

    /// <summary>Each key's versions, in S3's key order (UTF-8 bytes).</summary>
    public SortedDictionary<string, S3ObjectEntry> Objects { get; } = new(S3KeyComparer.Instance);

    /// <summary>The multipart uploads in progress, by upload id.</summary>
    public Dictionary<string, S3MultipartUpload> Uploads { get; } = new(StringComparer.Ordinal);

    /// <summary>Uploads completed, by upload id, whose abort S3 still answers as done.</summary>
    public HashSet<string> CompletedUploads { get; } = new(StringComparer.Ordinal);

    public List<Tag>? Tags { get; set; }

    /// <summary>Whether the bucket was created with Object Lock (which also turns versioning on for good).</summary>
    public bool ObjectLockEnabled { get; set; }

    /// <summary>Whether a new version gets its own version id (versioning enabled) rather than <c>null</c>.</summary>
    public bool VersioningEnabled => Versioning == "Enabled";

    /// <summary>The current (latest) version of a key, a delete marker included; null when the key has none.</summary>
    public S3ObjectVersion? Current(string key) =>
        Objects.TryGetValue(key, out var entry) && entry.Versions.Count > 0 ? entry.Versions[0] : null;

    /// <summary>A version of a key by id; null when there is none.</summary>
    public S3ObjectVersion? Version(string key, string versionId) =>
        Objects.TryGetValue(key, out var entry) ? entry.Versions.FirstOrDefault(v => v.VersionId == versionId) : null;

    /// <summary>
    /// Adds a new current version of its key. With versioning enabled it joins the key's versions;
    /// otherwise it replaces the key's <c>null</c> version (an unversioned or suspended bucket keeps
    /// one version without an id). Returns the version it replaced, if any.
    /// </summary>
    public S3ObjectVersion? Add(S3ObjectVersion version)
    {
        if (!Objects.TryGetValue(version.Key, out var entry))
        {
            entry = new S3ObjectEntry(version.Key);
            Objects.Add(version.Key, entry);
        }

        S3ObjectVersion? replaced = null;
        if (version.VersionId == S3ObjectVersion.NullVersionId)
        {
            var index = entry.Versions.FindIndex(v => v.VersionId == S3ObjectVersion.NullVersionId);
            if (index >= 0)
            {
                replaced = entry.Versions[index];
                entry.Versions.RemoveAt(index);
            }
        }

        entry.Versions.Insert(0, version);
        return replaced;
    }

    /// <summary>Removes one version of a key for good; the key goes when it has none left.</summary>
    public bool Remove(string key, string versionId)
    {
        if (!Objects.TryGetValue(key, out var entry))
        {
            return false;
        }

        var removed = entry.Versions.RemoveAll(v => v.VersionId == versionId) > 0;
        if (entry.Versions.Count == 0)
        {
            Objects.Remove(key);
        }

        return removed;
    }

    /// <summary>Whether the bucket holds anything that stops it being deleted: any version or delete marker.</summary>
    public bool IsEmpty => Objects.Count == 0;
}

/// <summary>One key of a bucket and its versions, newest first.</summary>
internal sealed class S3ObjectEntry
{
    public S3ObjectEntry(string key)
    {
        Key = key;
    }

    public string Key { get; }

    public List<S3ObjectVersion> Versions { get; } = [];
}

/// <summary>
/// One version of an object, or a delete marker: its bytes and everything S3 stores with them.
/// Versions are replaced, never edited, except for the settings S3 changes in place (tags, ACL,
/// retention, legal hold).
/// </summary>
internal sealed partial class S3ObjectVersion
{
    /// <summary>The version id of the version an unversioned (or suspended) bucket keeps.</summary>
    public const string NullVersionId = "null";

    public required string Key { get; init; }

    public required string VersionId { get; init; }

    public bool IsDeleteMarker { get; init; }

    public byte[] Data { get; init; } = [];

    public long Size => Data.LongLength;

    /// <summary>The ETag, quoted: the MD5 of the data, or of a multipart object's parts.</summary>
    public string ETag { get; init; } = "\"d41d8cd98f00b204e9800998ecf8427e\"";

    /// <summary>When the version was written, to the second.</summary>
    public DateTime LastModified { get; init; }

    public string? ContentType { get; init; }

    public string? ContentEncoding { get; init; }

    public string? ContentDisposition { get; init; }

    public string? ContentLanguage { get; init; }

    public string? CacheControl { get; init; }

    public string? Expires { get; init; }

    public string? WebsiteRedirectLocation { get; init; }

    /// <summary>User metadata (<c>x-amz-meta-*</c>), names in lower case.</summary>
    public Dictionary<string, string> Metadata { get; init; } = new(StringComparer.Ordinal);

    public string StorageClass { get; init; } = "STANDARD";

    /// <summary><c>AES256</c> unless the object was encrypted otherwise.</summary>
    public string ServerSideEncryption { get; init; } = "AES256";

    public string? SseKmsKeyId { get; init; }

    public bool? BucketKeyEnabled { get; init; }

    /// <summary>The SSE-C key's MD5, for an object encrypted with a customer key.</summary>
    public string? SseCustomerKeyMd5 { get; init; }

    /// <summary>The checksum algorithm (<c>CRC32</c>, ...), and the checksum S3 answers for the object.</summary>
    public string? ChecksumAlgorithm { get; init; }

    public string? Checksum { get; init; }

    /// <summary><c>FULL_OBJECT</c>, or <c>COMPOSITE</c> for a multipart object's checksum of its parts' checksums.</summary>
    public string? ChecksumType { get; init; }

    /// <summary>A multipart object's parts, in order; null for an object uploaded whole.</summary>
    public List<S3StoredPart>? Parts { get; init; }

    public List<Tag> Tags { get; set; } = [];

    /// <summary>The account that wrote the version (its owner).</summary>
    public string OwnerId { get; init; } = string.Empty;

    /// <summary>A copy of this version under another id or key, with every setting carried over.</summary>
    public S3ObjectVersion With(string key, string versionId, DateTime lastModified) => new()
    {
        Key = key,
        VersionId = versionId,
        IsDeleteMarker = IsDeleteMarker,
        Data = Data,
        ETag = ETag,
        LastModified = lastModified,
        ContentType = ContentType,
        ContentEncoding = ContentEncoding,
        ContentDisposition = ContentDisposition,
        ContentLanguage = ContentLanguage,
        CacheControl = CacheControl,
        Expires = Expires,
        WebsiteRedirectLocation = WebsiteRedirectLocation,
        Metadata = new Dictionary<string, string>(Metadata, StringComparer.Ordinal),
        StorageClass = StorageClass,
        ServerSideEncryption = ServerSideEncryption,
        SseKmsKeyId = SseKmsKeyId,
        BucketKeyEnabled = BucketKeyEnabled,
        SseCustomerKeyMd5 = SseCustomerKeyMd5,
        ChecksumAlgorithm = ChecksumAlgorithm,
        Checksum = Checksum,
        ChecksumType = ChecksumType,
        Parts = Parts,
        Tags = [.. Tags.Select(t => new Tag { Key = t.Key, Value = t.Value })],
        OwnerId = OwnerId,
    };
}

/// <summary>One part of a completed multipart object: where it lies in the data, and its checksums.</summary>
internal sealed class S3StoredPart
{
    public int PartNumber { get; init; }

    public long Offset { get; init; }

    public long Size { get; init; }

    public string ETag { get; init; } = string.Empty;

    /// <summary>The part's checksum in the upload's algorithm; null when the upload had none.</summary>
    public string? Checksum { get; init; }
}

/// <summary>A multipart upload in progress: what CreateMultipartUpload set, and the parts uploaded so far.</summary>
internal sealed partial class S3MultipartUpload
{
    public required string UploadId { get; init; }

    public required string Key { get; init; }

    public DateTime Initiated { get; init; }

    public string? ContentType { get; init; }

    public string? ContentEncoding { get; init; }

    public string? ContentDisposition { get; init; }

    public string? ContentLanguage { get; init; }

    public string? CacheControl { get; init; }

    public string? Expires { get; init; }

    public string? WebsiteRedirectLocation { get; init; }

    public Dictionary<string, string> Metadata { get; init; } = new(StringComparer.Ordinal);

    public string StorageClass { get; init; } = "STANDARD";

    public string ServerSideEncryption { get; init; } = "AES256";

    public string? SseKmsKeyId { get; init; }

    public bool? BucketKeyEnabled { get; init; }

    public string? SseCustomerKeyMd5 { get; init; }

    public List<Tag> Tags { get; init; } = [];

    /// <summary>The checksum algorithm the upload was created with; null for none.</summary>
    public string? ChecksumAlgorithm { get; init; }

    /// <summary><c>COMPOSITE</c> or <c>FULL_OBJECT</c>, when the upload has a checksum algorithm.</summary>
    public string? ChecksumType { get; init; }

    public string OwnerId { get; init; } = string.Empty;

    /// <summary>The parts uploaded so far, by part number; a part uploaded again replaces the earlier one.</summary>
    public SortedDictionary<int, S3UploadedPart> Parts { get; } = [];
}

/// <summary>One part of a multipart upload in progress.</summary>
internal sealed class S3UploadedPart
{
    public int PartNumber { get; init; }

    public byte[] Data { get; init; } = [];

    public string ETag { get; init; } = string.Empty;

    public DateTime LastModified { get; init; }

    /// <summary>The part's checksums, by algorithm, as sent or computed.</summary>
    public Dictionary<string, string> Checksums { get; init; } = new(StringComparer.Ordinal);
}

/// <summary>
/// S3's key order: by the keys' UTF-8 bytes, which is code-point order. UTF-16 ordinal order differs
/// only for characters past U+FFFF against those from U+E000 to U+FFFF.
/// </summary>
internal sealed class S3KeyComparer : IComparer<string>
{
    public static readonly S3KeyComparer Instance = new();

    public int Compare(string? x, string? y)
    {
        if (ReferenceEquals(x, y))
        {
            return 0;
        }

        if (x is null)
        {
            return -1;
        }

        if (y is null)
        {
            return 1;
        }

        var length = Math.Min(x.Length, y.Length);
        for (var i = 0; i < length; i++)
        {
            var a = x[i];
            var b = y[i];
            if (a != b)
            {
                return Weight(a).CompareTo(Weight(b));
            }
        }

        return x.Length.CompareTo(y.Length);
    }

    /// <summary>Surrogates (D800-DFFF) sort after every other UTF-16 unit, as their code points do.</summary>
    private static int Weight(char c) => c >= 0xD800 && c <= 0xDFFF ? c + 0x2000 : c >= 0xE000 ? c - 0x800 : c;
}
