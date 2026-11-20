// Translated from behavioral/s3/check_op_contract_get_bucket_versioning.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.op-contract-getBucketVersioning</summary>
[Category("AwsEvaluationSuite")]
public sealed class OpContractGetBucketVersioning
{
    private const string BucketPrefix = "sim2real-behav-";

    // S3's refusal of a bucket that does not exist, as real S3 returns it.
    private static readonly (string?, string?) NoSuchBucket = ("NoSuchBucket", "The specified bucket does not exist");

    /// <summary>GetBucketVersioning reflects Enabled/Suspended transitions; errors NoSuchBucket for a missing bucket.</summary>
    [Test]
    [DisplayName("s3.op-contract-getBucketVersioning")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        // This check's _create_bucket (its bucket prefix is sim2real-behav-) tags nothing.
        var bucket = await S3Fidelity.CreateBucket(ctx);
        var s3 = ctx.S3;

        // a fresh bucket has no versioning configured -> empty response (no Status key), not an error
        // (the .NET SDK reports an absent Status as "Off"; the raw response shows it is absent)
        var raw = await S3CoreFidelity.RawBucketSubresource(ctx, bucket, "versioning");
        Ensure(!raw.Contains("<Status>", StringComparison.Ordinal), $"expected no Status on unversioned bucket, got {Py.Repr(raw)}");
        var resp0 = await s3.GetBucketVersioningAsync(new GetBucketVersioningRequest { BucketName = bucket });
        Ensure(resp0.VersioningConfig.Status == VersionStatus.Off, $"expected no Status on unversioned bucket, got {Py.Repr(resp0.VersioningConfig.Status?.Value)}");

        await Put(s3, bucket, VersionStatus.Enabled);
        var resp1 = await s3.GetBucketVersioningAsync(new GetBucketVersioningRequest { BucketName = bucket });
        Ensure(resp1.VersioningConfig.Status == VersionStatus.Enabled, $"expected Enabled, got {Py.Repr(resp1.VersioningConfig.Status?.Value)}");

        await Put(s3, bucket, VersionStatus.Suspended);
        var resp2 = await s3.GetBucketVersioningAsync(new GetBucketVersioningRequest { BucketName = bucket });
        Ensure(resp2.VersioningConfig.Status == VersionStatus.Suspended, $"expected Suspended, got {Py.Repr(resp2.VersioningConfig.Status?.Value)}");

        // distinct error boundary: nonexistent bucket
        var missing = FidelityContext.UniqueName(BucketPrefix + "missing-");
        var err = await FidelityContext.CaptureError(() => s3.GetBucketVersioningAsync(new GetBucketVersioningRequest { BucketName = missing }))
            ?? throw Fail("expected a ClientError, but the call SUCCEEDED");
        Ensure((err.Code, err.Message) == NoSuchBucket, $"expected NoSuchBucket, got {err}");
    }

    private static Task Put(IAmazonS3 s3, string bucket, VersionStatus status) =>
        s3.PutBucketVersioningAsync(new PutBucketVersioningRequest
        {
            BucketName = bucket,
            VersioningConfig = new S3BucketVersioningConfig { Status = status },
        });
}
