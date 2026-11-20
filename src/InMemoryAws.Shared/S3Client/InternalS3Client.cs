using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using InMemoryAws.Http;
using InMemoryAws.S3.Model;

namespace InMemoryAws.S3;

/// <summary>
/// In-memory S3, serving the generated dispatcher's requests. Overrides <see cref="S3ClientBase"/>
/// operation by operation; an operation it doesn't override answers <c>NotImplemented</c>.
/// </summary>
/// <remarks>
/// Split into partial files by area: buckets, objects, listing, multipart uploads, tagging. Every
/// operation holds the cloud's <see cref="S3Store.Gate"/>. Buckets are global, but each was created
/// in a region, and a request signed for another region is refused, as S3 refuses it.
/// </remarks>
internal sealed partial class InternalS3Client : S3ClientBase
{
    private static readonly RandomNumberGenerator Random = RandomNumberGenerator.Create();

    private readonly InMemoryAwsCloud _cloud;

    /// <param name="cloud">The cloud holding the buckets.</param>
    /// <param name="region">The region the request was signed for or sent to; null for the cloud's current region.</param>
    internal InternalS3Client(InMemoryAwsCloud cloud, string? region = null)
    {
        _cloud = cloud;
        Region = string.IsNullOrEmpty(region) ? cloud.CurrentRegion : region!;
    }

    /// <summary>The request being served, when it came over HTTP: its raw headers and query, for what the model doesn't bind.</summary>
    internal S3WireRequest? Request { get; init; }

    /// <summary>The region the request was made in.</summary>
    internal string Region { get; }

    private S3Store Store => _cloud.S3;

    /// <summary>The time now on the cloud's clock.</summary>
    private DateTime Now => _cloud.TimeProvider.GetUtcNow().UtcDateTime;

    /// <summary>The time now, to the second, as S3 records <c>LastModified</c>.</summary>
    private DateTime NowToTheSecond
    {
        get
        {
            var now = Now;
            return new DateTime(now.Ticks - (now.Ticks % TimeSpan.TicksPerSecond), DateTimeKind.Utc);
        }
    }

    /// <summary>The canonical user id of the cloud's account: 64 hex digits, stable per account.</summary>
    internal string OwnerId => CanonicalUserId(_cloud.CurrentAccountId);

    internal static string CanonicalUserId(string accountId)
    {
        return S3Checksums.Hex(SHA256.HashData(Encoding.UTF8.GetBytes("s3-owner:" + accountId)));
    }

    private Owner Owner(string? ownerId = null) => new() { ID = ownerId ?? OwnerId };

    /// <summary>
    /// The bucket a request names, under the store's lock: <c>NoSuchBucket</c> when there is none, and
    /// refused when the request was signed for another region than the bucket's, or when the bucket's
    /// access settings refuse it (see <see cref="Authorize"/>).
    /// </summary>
    private S3Bucket GetBucket(string? name)
    {
        if (string.IsNullOrEmpty(name) || !Store.Buckets.TryGetValue(name!, out var bucket))
        {
            throw S3Errors.NoSuchBucket(name);
        }

        CheckRegion(bucket);
        Authorize(bucket);
        AuthorizeSession(bucket);
        return bucket;
    }

    /// <summary>Checks the S3 Express session a request to a directory bucket is signed with.</summary>
    partial void AuthorizeSession(S3Bucket bucket);

    /// <summary>
    /// A request signed for another region than the bucket's is refused: S3 answers it as a malformed
    /// authorization header naming the bucket's region (a HEAD request, which has no body, with a
    /// redirect), and sends the bucket's region as <c>x-amz-bucket-region</c>, so SDKs can retry there.
    /// </summary>
    private void CheckRegion(S3Bucket bucket)
    {
        if (Request?.Region is not { } signed || string.Equals(signed, bucket.Region, StringComparison.Ordinal))
        {
            return;
        }

        if (Request.Method == "HEAD")
        {
            throw new S3Exception("PermanentRedirect", "Moved Permanently", System.Net.HttpStatusCode.MovedPermanently)
                .WithHeader("x-amz-bucket-region", bucket.Region);
        }

        throw new S3Exception(
                "AuthorizationHeaderMalformed",
                $"The authorization header is malformed; the region '{signed}' is wrong; expecting '{bucket.Region}'",
                System.Net.HttpStatusCode.BadRequest,
                ("Region", bucket.Region))
            .WithHeader("x-amz-bucket-region", bucket.Region);
    }

    /// <summary>A new version id: 32 URL-safe characters, as S3 mints them.</summary>
    private static string NewVersionId() => S3VersionIds.New(Random);

    /// <summary>A new multipart upload id.</summary>
    private static string NewUploadId() => RandomToken(60);

    private static string RandomToken(int bytes)
    {
        var buffer = new byte[bytes];
        Random.GetBytes(buffer);
        return Convert.ToBase64String(buffer).Replace('+', '.').Replace('/', '_').TrimEnd('=');
    }

    /// <summary>The version id a new version of an object in <paramref name="bucket"/> gets.</summary>
    private static string NextVersionId(S3Bucket bucket) => bucket.VersioningEnabled ? NewVersionId() : S3ObjectVersion.NullVersionId;

    /// <summary>The <c>x-amz-version-id</c> S3 answers for a version: none in a bucket that was never versioned.</summary>
    private static string? VersionIdHeader(S3Bucket bucket, S3ObjectVersion version) =>
        bucket.Versioning is null ? null : version.VersionId;

    private static string Invariant(long value) => value.ToString(CultureInfo.InvariantCulture);
}
