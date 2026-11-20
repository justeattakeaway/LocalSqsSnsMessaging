// Translated from behavioral/s3/check_get_bucket_versioning_unset_fresh.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.get-bucket-versioning-unset-fresh</summary>
[Category("AwsEvaluationSuite")]
public sealed class GetBucketVersioningUnsetFresh
{
    /// <summary>A fresh bucket has NO versioning status at all -- 'unset' is a third state distinct from Enabled/Suspended.</summary>
    [Test]
    [DisplayName("s3.get-bucket-versioning-unset-fresh")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var bucket = await S3Fidelity.CreateBucket(ctx, "sim2real-behav-bkt-");
        // The .NET SDK reports an absent Status as "Off" and an absent MFADelete as null; the raw
        // response says whether the elements were there at all.
        var raw = await S3CoreFidelity.RawBucketSubresource(ctx, bucket, "versioning");
        Ensure(!raw.Contains("<Status>", StringComparison.Ordinal), $"fresh bucket must report no versioning Status, got {Py.Repr(raw)}");
        Ensure(!raw.Contains("<MfaDelete>", StringComparison.Ordinal), $"fresh bucket must report no MFADelete, got {Py.Repr(raw)}");
        var resp = await s3.GetBucketVersioningAsync(new GetBucketVersioningRequest { BucketName = bucket });
        Ensure(resp.VersioningConfig.Status == VersionStatus.Off, $"fresh bucket must report no versioning Status, got {Py.Repr(resp.VersioningConfig.Status?.Value)}");
        Ensure(resp.VersioningConfig.EnableMfaDelete is null or false, $"fresh bucket must report no MFADelete, got {resp.VersioningConfig.EnableMfaDelete}");

        // ... and it becomes observable only after an explicit transition
        await s3.PutBucketVersioningAsync(new PutBucketVersioningRequest
        {
            BucketName = bucket,
            VersioningConfig = new S3BucketVersioningConfig { Status = VersionStatus.Enabled },
        });
        var after = await s3.GetBucketVersioningAsync(new GetBucketVersioningRequest { BucketName = bucket });
        Ensure(
            after.VersioningConfig.Status == VersionStatus.Enabled,
            $"expected Status='Enabled' after PutBucketVersioning, got {Py.Repr(after.VersioningConfig.Status?.Value)}");
    }
}
