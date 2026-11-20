// Translated from behavioral/s3/check_get_object_nonexistent_version_errors.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Text;
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.get-object-nonexistent-version-errors</summary>
[Category("AwsEvaluationSuite")]
public sealed class GetObjectNonexistentVersionErrors
{
    /// <summary>
    /// GetObject distinguishes a malformed VersionId (InvalidArgument) from a well-formed but
    /// no-longer-existing one (NoSuchVersion).
    /// </summary>
    [Test]
    [DisplayName("s3.get-object-nonexistent-version-errors")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var bucket = await S3Fidelity.CreateBucket(ctx, "sim2real-behav-bkt-");
        await s3.PutBucketVersioningAsync(new PutBucketVersioningRequest
        {
            BucketName = bucket,
            VersioningConfig = new S3BucketVersioningConfig { Status = VersionStatus.Enabled },
        });
        var v1 = (await Put(s3, bucket, "v1")).VersionId;
        var v2 = (await Put(s3, bucket, "v2")).VersionId;

        // A structurally invalid version id is rejected as a bad argument, NOT as a 404.
        var malformed = v1[..^1] + (v1[^1] != 'A' ? "A" : "B");
        var err = await ClientError(() => Get(s3, bucket, malformed));
        Ensure(err.Code == "InvalidArgument", $"expected InvalidArgument for a malformed VersionId, got {err}");
        Ensure(err.Message == "Invalid version id specified", err.ToString());

        // Permanently remove v1, then that (well-formed, previously valid) id is NoSuchVersion.
        await s3.DeleteObjectAsync(new DeleteObjectRequest { BucketName = bucket, Key = "k", VersionId = v1 });
        err = await ClientError(() => Get(s3, bucket, v1));
        Ensure(err.Code == "NoSuchVersion", $"expected NoSuchVersion for a permanently deleted version, got {err}");
        Ensure(err.Message == "The specified version does not exist.", err.ToString());

        // the surviving version is untouched, and this is the positive control: it proves GetObject
        // with a well-formed, existing VersionId works at all, so the two rejections above are specific
        // to the malformed/deleted input rather than the target refusing every versioned read.
        var body = Encoding.UTF8.GetString(await Get(s3, bucket, v2));
        Ensure(body == "v2", $"expected surviving version v2 body b'v2', got {Py.Repr(body)}");
    }

    private static Task<PutObjectResponse> Put(IAmazonS3 s3, string bucket, string body) =>
        s3.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = "k", InputStream = new MemoryStream(Encoding.UTF8.GetBytes(body)) });

    private static async Task<byte[]> Get(IAmazonS3 s3, string bucket, string versionId)
    {
        using var response = await s3.GetObjectAsync(new GetObjectRequest { BucketName = bucket, Key = "k", VersionId = versionId });
        return await S3Fidelity.ReadBody(response);
    }

    /// <summary>
    /// Call fn(); return the botocore Error dict. An unexpected SUCCESS raises, because an emulator
    /// ACCEPTING input real AWS refuses is the strongest signal available.
    /// </summary>
    private static async Task<AwsError> ClientError(Func<Task> fn) =>
        await FidelityContext.CaptureError(fn) ?? throw Fail("expected a ClientError, but the call SUCCEEDED");
}
