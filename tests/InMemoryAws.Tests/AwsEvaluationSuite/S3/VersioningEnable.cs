// Translated from behavioral/s3/check_versioning_enable.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.versioning-enable</summary>
[Category("AwsEvaluationSuite")]
public sealed class VersioningEnable
{
    /// <summary>PutBucketVersioning(Enabled) is reflected in GetBucketVersioning.</summary>
    [Test]
    [DisplayName("s3.versioning-enable")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var bucket = await S3Fidelity.CreateBucket(ctx, "sim2real-behav-bkt-");
        await S3Fidelity.SetVersioning(s3, bucket, VersionStatus.Enabled);
        var resp = await s3.GetBucketVersioningAsync(new GetBucketVersioningRequest { BucketName = bucket });
        Ensure(resp.VersioningConfig.Status == VersionStatus.Enabled, $"expected Enabled, got {Py.Repr(resp.VersioningConfig.Status?.Value)}");
    }
}
