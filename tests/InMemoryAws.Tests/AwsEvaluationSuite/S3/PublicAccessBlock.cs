// Translated from behavioral/s3/check_public_access_block.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.public-access-block</summary>
[Category("AwsEvaluationSuite")]
public sealed class PublicAccessBlock
{
    /// <summary>
    /// GetPublicAccessBlock returns each of the four settings exactly as PutPublicAccessBlock set them;
    /// the configuration is not all-True, so a target that ignores the PUT and serves the default fails.
    /// </summary>
    [Test]
    [DisplayName("s3.public-access-block")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var bucket = await S3Fidelity.CreateBucket(ctx, "sim2real-behav-bkt-");
        await s3.PutPublicAccessBlockAsync(new PutPublicAccessBlockRequest
        {
            BucketName = bucket,
            PublicAccessBlockConfiguration = new PublicAccessBlockConfiguration
            {
                BlockPublicAcls = false,
                IgnorePublicAcls = true,
                BlockPublicPolicy = false,
                RestrictPublicBuckets = true,
            },
        });
        var got = (await s3.GetPublicAccessBlockAsync(new GetPublicAccessBlockRequest { BucketName = bucket })).PublicAccessBlockConfiguration;
        foreach (var (key, val, actual) in new (string, bool, bool?)[]
        {
            ("BlockPublicAcls", false, got.BlockPublicAcls),
            ("IgnorePublicAcls", true, got.IgnorePublicAcls),
            ("BlockPublicPolicy", false, got.BlockPublicPolicy),
            ("RestrictPublicBuckets", true, got.RestrictPublicBuckets),
        })
        {
            Ensure(actual == val, $"{key}: expected {val}, got {actual}");
        }
    }
}
