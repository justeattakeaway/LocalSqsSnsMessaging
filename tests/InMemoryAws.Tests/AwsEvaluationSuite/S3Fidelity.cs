using Amazon.S3;
using Amazon.S3.Model;

namespace InMemoryAws.Tests.AwsEvaluationSuite;

/// <summary>
/// What the S3 ports share: the checks' <c>_create_bucket</c> helper (a bucket in the check's region,
/// with every version, delete marker and multipart upload removed at cleanup), and reading an
/// object's body as boto3's <c>resp["Body"].read()</c> does.
/// </summary>
public static partial class S3Fidelity
{
    /// <summary>
    /// <c>_create_bucket(ctx)</c>: a general purpose bucket named <paramref name="prefix"/> plus 12 hex
    /// digits, created with the region's <c>LocationConstraint</c> (none in <c>us-east-1</c>), emptied
    /// and deleted at cleanup.
    /// </summary>
    public static async Task<string> CreateBucket(
        FidelityContext ctx,
        string prefix = "sim2real-behav-",
        AmazonS3Client? client = null,
        bool objectLockEnabled = false,
        string? region = null)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        var s3 = client ?? (region is null ? ctx.S3 : ctx.S3Client(region));
        region ??= ctx.Region;
        var name = FidelityContext.UniqueName(prefix);
        var request = new PutBucketRequest { BucketName = name, UseClientRegion = false };
        if (region != "us-east-1")
        {
            request.PutBucketConfiguration = new PutBucketConfiguration
            {
                LocationConstraint = new BucketLocationConstraint(region),
            };
        }

        if (objectLockEnabled)
        {
            request.ObjectLockEnabledForBucket = true;
        }

        await s3.PutBucketAsync(request);
        ctx.OnCleanup(() => EmptyAndDelete(s3, name));
        return name;
    }

    /// <summary>
    /// The checks' bucket cleanup: deletes every version and delete marker, aborts every multipart
    /// upload, then deletes the bucket, ignoring a bucket that is already gone.
    /// </summary>
    public static async Task EmptyAndDelete(IAmazonS3 s3, string bucket)
    {
        ArgumentNullException.ThrowIfNull(s3);
        try
        {
            var versions = await s3.ListVersionsAsync(new ListVersionsRequest { BucketName = bucket });
            foreach (var version in versions.Versions ?? [])
            {
                await s3.DeleteObjectAsync(new DeleteObjectRequest
                {
                    BucketName = bucket,
                    Key = version.Key,
                    VersionId = version.VersionId,
                    BypassGovernanceRetention = true,
                });
            }
        }
        catch (AmazonS3Exception ex) when (ex.ErrorCode is "NoSuchBucket" or "NoSuchKey")
        {
        }

        try
        {
            var uploads = await s3.ListMultipartUploadsAsync(new ListMultipartUploadsRequest { BucketName = bucket });
            foreach (var upload in uploads.MultipartUploads ?? [])
            {
                await s3.AbortMultipartUploadAsync(bucket, upload.Key, upload.UploadId);
            }
        }
        catch (AmazonS3Exception ex) when (ex.ErrorCode is "NoSuchBucket" or "NoSuchKey" or "NoSuchUpload")
        {
        }

        try
        {
            await s3.DeleteBucketAsync(bucket);
        }
        catch (AmazonS3Exception ex) when (ex.ErrorCode == "NoSuchBucket")
        {
        }
    }

    /// <summary><c>resp["Body"].read()</c>: the whole body of a GetObject response.</summary>
    public static async Task<byte[]> ReadBody(GetObjectResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);
        using var memory = new MemoryStream();
        await response.ResponseStream.CopyToAsync(memory);
        return memory.ToArray();
    }

    /// <summary><c>s3.get_object(Bucket=..., Key=...)["Body"].read()</c>.</summary>
    public static async Task<byte[]> GetBytes(IAmazonS3 s3, string bucket, string key)
    {
        ArgumentNullException.ThrowIfNull(s3);
        using var response = await s3.GetObjectAsync(bucket, key);
        return await ReadBody(response);
    }
}
