// Translated from behavioral/s3/check_op_contract_put_bucket_versioning.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.op-contract-putBucketVersioning</summary>
[Category("AwsEvaluationSuite")]
public sealed class OpContractPutBucketVersioning
{
    private const string BucketPrefix = "sim2real-behav-";

    // S3's refusal of a bucket that does not exist, as real S3 returns it.
    private static readonly (string?, string?) NoSuchBucket = ("NoSuchBucket", "The specified bucket does not exist");

    /// <summary>PutBucketVersioning drives Unversioned -> Enabled -> Suspended; errors NoSuchBucket when missing.</summary>
    [Test]
    [DisplayName("s3.op-contract-putBucketVersioning")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var bucket = await S3Fidelity.CreateBucket(ctx);
        var s3 = ctx.S3;

        // (the .NET SDK reports an absent Status as "Off"; the raw response shows it is absent)
        var resp0 = await S3CoreFidelity.RawBucketSubresource(ctx, bucket, "versioning");
        Ensure(!resp0.Contains("<Status>", StringComparison.Ordinal), $"expected no Status on fresh bucket, got {Py.Repr(resp0)}");

        await Put(s3, bucket, VersionStatus.Enabled);
        var status1 = (await s3.GetBucketVersioningAsync(new GetBucketVersioningRequest { BucketName = bucket })).VersioningConfig.Status?.Value;
        Ensure(status1 == "Enabled", $"expected Enabled, got {status1}");

        // while enabled, writes actually produce distinct object versions
        const string key = "versioning-state.txt";
        var v1 = (await s3.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = key, ContentBody = "first" })).VersionId;
        var v2 = (await s3.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = key, ContentBody = "second" })).VersionId;
        Ensure(!string.IsNullOrEmpty(v1) && !string.IsNullOrEmpty(v2) && v1 != v2, $"expected two distinct non-empty version ids, got {Py.Repr(v1)} and {Py.Repr(v2)}");

        await Put(s3, bucket, VersionStatus.Suspended);
        var status2 = (await s3.GetBucketVersioningAsync(new GetBucketVersioningRequest { BucketName = bucket })).VersioningConfig.Status?.Value;
        Ensure(status2 == "Suspended", $"expected Suspended, got {status2}");

        // while suspended, new writes to the same key do not create a new distinct version id
        await s3.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = key, ContentBody = "third" });
        var head = await s3.GetObjectMetadataAsync(bucket, key);
        Ensure(head.VersionId is null or "null", $"expected null version id while suspended, got {head.VersionId}");

        ctx.OnCleanup(() => s3.DeleteObjectAsync(bucket, key));
        ctx.OnCleanup(() => s3.DeleteObjectAsync(new DeleteObjectRequest { BucketName = bucket, Key = key, VersionId = v1 }));
        ctx.OnCleanup(() => s3.DeleteObjectAsync(new DeleteObjectRequest { BucketName = bucket, Key = key, VersionId = v2 }));

        // distinct error boundary: nonexistent bucket
        var missing = FidelityContext.UniqueName(BucketPrefix + "missing-");
        var code = await S3Fidelity.ErrorPair(() => Put(s3, missing, VersionStatus.Enabled));
        Ensure(code == NoSuchBucket, $"expected NoSuchBucket, got {S3Fidelity.Repr(code)}");
    }

    private static Task Put(IAmazonS3 s3, string bucket, VersionStatus status) =>
        s3.PutBucketVersioningAsync(new PutBucketVersioningRequest
        {
            BucketName = bucket,
            VersioningConfig = new S3BucketVersioningConfig { Status = status },
        });
}
