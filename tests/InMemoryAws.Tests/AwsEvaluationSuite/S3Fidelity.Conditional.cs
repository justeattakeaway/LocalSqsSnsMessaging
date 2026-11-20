using System.Security.Cryptography;
using System.Text;
using Amazon.S3;
using Amazon.S3.Model;

namespace InMemoryAws.Tests.AwsEvaluationSuite;

/// <summary>
/// What the conditional-write ports (<c>conc_s3_conditional_writes_*</c>) share: their bucket
/// (<c>_new_bucket</c>, prefix <c>sim2real-condw-</c>), starting an upload whose abort is a cleanup,
/// uploading its parts, the deterministic <c>_pattern</c> bodies, and the listings they compare.
/// </summary>
public static partial class S3Fidelity
{
    /// <summary>The conditional-write checks' <c>_new_bucket(ctx, s3)</c>.</summary>
    public static Task<string> CreateCondwBucket(FidelityContext ctx) => CreateBucket(ctx, "sim2real-condw-");

    /// <summary>
    /// <c>_start_upload(ctx, s3, bucket, key)</c>: CreateMultipartUpload, with an abort (ignoring
    /// <c>NoSuchUpload</c>) as a cleanup.
    /// </summary>
    public static async Task<string> StartUpload(FidelityContext ctx, IAmazonS3 s3, string bucket, string key)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentNullException.ThrowIfNull(s3);
        var uploadId = (await s3.InitiateMultipartUploadAsync(new InitiateMultipartUploadRequest { BucketName = bucket, Key = key })).UploadId;
        ctx.OnCleanup(AbortQuietly(s3, bucket, key, uploadId));
        return uploadId;
    }

    /// <summary>
    /// <c>_upload_parts(s3, bucket, key, upload_id, parts)</c>: uploads each body as parts 1, 2, ...
    /// and returns <c>{"PartNumber": n, "ETag": etag}</c> for each.
    /// </summary>
    public static async Task<List<PartETag>> UploadParts(IAmazonS3 s3, string bucket, string key, string uploadId, params byte[][] parts)
    {
        ArgumentNullException.ThrowIfNull(s3);
        ArgumentNullException.ThrowIfNull(parts);
        var result = new List<PartETag>();
        for (var i = 0; i < parts.Length; i++)
        {
            var etag = (await s3.UploadPartAsync(new UploadPartRequest
            {
                BucketName = bucket,
                Key = key,
                UploadId = uploadId,
                PartNumber = i + 1,
                InputStream = new MemoryStream(parts[i]),
            })).ETag;
            result.Add(Part(i + 1, etag));
        }

        return result;
    }

    /// <summary>
    /// <c>_pattern(tag, size)</c>: the SHA-256 of <paramref name="tag"/> repeated to
    /// <paramref name="size"/> bytes.
    /// </summary>
    public static byte[] Pattern(string tag, int size)
    {
        var unit = SHA256.HashData(Encoding.UTF8.GetBytes(tag));
        var data = new byte[size];
        for (var i = 0; i < size; i++)
        {
            data[i] = unit[i % unit.Length];
        }

        return data;
    }

    /// <summary><c>_md5_etag(body)</c>: the quoted hex MD5 of <paramref name="body"/>.</summary>
    public static string Md5ETag(byte[] body) => "\"" + Md5Hex(body) + "\"";

    /// <summary>
    /// <c>_multipart_etag(parts)</c>: the ETag S3 gives a multipart object, the MD5 of the parts'
    /// binary MD5s, then <c>-</c> and the part count, quoted.
    /// </summary>
    public static string MultipartETag(params byte[][] parts)
    {
        ArgumentNullException.ThrowIfNull(parts);
        var digest = Md5Hex([.. parts.SelectMany(Md5)]);
        return $"\"{digest}-{parts.Length}\"";
    }

    /// <summary>
    /// <c>_multipart_put(ctx, s3, bucket, key, parts)</c>: uploads <paramref name="parts"/> as one
    /// multipart object at <paramref name="key"/> and returns CompleteMultipartUpload's ETag.
    /// </summary>
    public static async Task<string> MultipartPut(FidelityContext ctx, IAmazonS3 s3, string bucket, string key, params byte[][] parts)
    {
        var uploadId = await StartUpload(ctx, s3, bucket, key);
        var done = await UploadParts(s3, bucket, key, uploadId, parts);
        return (await s3.CompleteMultipartUploadAsync(new CompleteMultipartUploadRequest { BucketName = bucket, Key = key, UploadId = uploadId, PartETags = done })).ETag;
    }

    /// <summary>
    /// <c>_second_region(ctx)</c>: the first region, sorted, other than the check's that the account
    /// can use without opting in.
    /// </summary>
    public static string SecondRegion(FidelityContext ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        var usable = DefaultEnabledRegions.Where(r => r != ctx.Region).Order(StringComparer.Ordinal).ToList();
        FidelityAssert.Ensure(usable.Count > 0, $"describe_regions offered no enabled region other than {Py.Repr(ctx.Region)}");
        return usable[0];
    }

    /// <summary><c>hashlib.sha256(data).hexdigest()</c>.</summary>
    public static string Sha256Hex(byte[] data) => Convert.ToHexStringLower(SHA256.HashData(data));

    /// <summary>
    /// <c>[(o["Key"], o["ETag"], o["Size"]) for o in Contents]</c> over every ListObjectsV2 page.
    /// </summary>
    public static async Task<List<(string Key, string ETag, long Size)>> ListedObjects(IAmazonS3 s3, string bucket)
    {
        ArgumentNullException.ThrowIfNull(s3);
        var listed = new List<(string, string, long)>();
        string? token = null;
        do
        {
            var page = await s3.ListObjectsV2Async(new ListObjectsV2Request { BucketName = bucket, ContinuationToken = token });
            listed.AddRange((page.S3Objects ?? []).Select(o => (o.Key, o.ETag, o.Size ?? 0)));
            token = page.IsTruncated == true ? page.NextContinuationToken : null;
        }
        while (token is not null);
        return listed;
    }

    /// <summary><c>_all_versions(s3, bucket)</c>: every version and every delete marker, over all pages.</summary>
    public static async Task<(List<S3ObjectVersion> Versions, List<S3ObjectVersion> Markers)> AllVersions(IAmazonS3 s3, string bucket)
    {
        ArgumentNullException.ThrowIfNull(s3);
        var versions = new List<S3ObjectVersion>();
        var markers = new List<S3ObjectVersion>();
        string? keyMarker = null;
        string? versionMarker = null;
        while (true)
        {
            var page = await s3.ListVersionsAsync(new ListVersionsRequest { BucketName = bucket, KeyMarker = keyMarker, VersionIdMarker = versionMarker });
            foreach (var version in page.Versions ?? [])
            {
                (version.IsDeleteMarker == true ? markers : versions).Add(version);
            }

            if (page.IsTruncated != true)
            {
                return (versions, markers);
            }

            keyMarker = page.NextKeyMarker;
            versionMarker = page.NextVersionIdMarker;
        }
    }

    /// <summary><c>_listed_parts</c>: <c>(PartNumber, ChecksumSHA256)</c> of every part, every page.</summary>
    public static async Task<List<(int, string?)>> ListedPartsSha256(IAmazonS3 s3, string bucket, string key, string uploadId)
    {
        ArgumentNullException.ThrowIfNull(s3);
        var parts = new List<(int, string?)>();
        int? marker = null;
        while (true)
        {
            var page = await s3.ListPartsAsync(new ListPartsRequest { BucketName = bucket, Key = key, UploadId = uploadId, PartNumberMarker = marker?.ToString(System.Globalization.CultureInfo.InvariantCulture) });
            parts.AddRange((page.Parts ?? []).Select(p => (p.PartNumber ?? 0, (string?)p.ChecksumSHA256)));
            if (page.IsTruncated != true)
            {
                return parts;
            }

            marker = page.NextPartNumberMarker;
        }
    }
}
