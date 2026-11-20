// Translated from behavioral/s3/check_bucket_accelerate_configuration_roundtrip.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.bucket-accelerate-configuration-roundtrip</summary>
[Category("AwsEvaluationSuite")]
public sealed class BucketAccelerateConfigurationRoundtrip
{
    /// <summary>
    /// PutBucketAccelerateConfiguration enables/suspends transfer acceleration and
    /// GetBucketAccelerateConfiguration reflects the current status at each step.
    /// </summary>
    [Test]
    [DisplayName("s3.bucket-accelerate-configuration-roundtrip")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var name = await S3Fidelity.CreateBucket(ctx, "sim2real-behav-accel-");
        await s3.PutBucketAccelerateConfigurationAsync(new PutBucketAccelerateConfigurationRequest
        {
            BucketName = name,
            AccelerateConfiguration = new AccelerateConfiguration { Status = BucketAccelerateStatus.Enabled },
        });
        var got = await s3.GetBucketAccelerateConfigurationAsync(new GetBucketAccelerateConfigurationRequest { BucketName = name });
        Ensure(got.Status == BucketAccelerateStatus.Enabled, Py.Repr(got.Status?.Value));
        await s3.PutBucketAccelerateConfigurationAsync(new PutBucketAccelerateConfigurationRequest
        {
            BucketName = name,
            AccelerateConfiguration = new AccelerateConfiguration { Status = BucketAccelerateStatus.Suspended },
        });
        var got2 = await s3.GetBucketAccelerateConfigurationAsync(new GetBucketAccelerateConfigurationRequest { BucketName = name });
        Ensure(got2.Status == BucketAccelerateStatus.Suspended, Py.Repr(got2.Status?.Value));
    }
}
