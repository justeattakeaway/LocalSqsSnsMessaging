// Translated from behavioral/s3/check_op_contract_get_public_access_block.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.op-contract-getPublicAccessBlock</summary>
[Category("AwsEvaluationSuite")]
public sealed class OpContractGetPublicAccessBlock
{
    /// <summary>S3's refusal of a bucket with no public access block, as real S3 returns it.</summary>
    private static readonly (string, string) NoSuchPab = ("NoSuchPublicAccessBlockConfiguration", "The public access block configuration was not found");

    /// <summary>GetPublicAccessBlock returns the exact configuration put; errors when none is set.</summary>
    [Test]
    [DisplayName("s3.op-contract-getPublicAccessBlock")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var bucket = await S3Fidelity.CreateBucket(ctx, "sim2real-behav-");
        var s3 = ctx.S3;

        // new buckets are created with a default "block all public access" configuration
        // (S3's April 2023 secure-by-default change); clear it first so the "no
        // configuration set" boundary can actually be observed.
        await FidelityContext.CaptureError(() => s3.DeletePublicAccessBlockAsync(new DeletePublicAccessBlockRequest { BucketName = bucket }));

        // distinct error boundary: no configuration set yet
        var error = await S3Fidelity.Error(() => s3.GetPublicAccessBlockAsync(new GetPublicAccessBlockRequest { BucketName = bucket }));
        Ensure((error.Code, error.Message) == NoSuchPab, $"expected NoSuchPublicAccessBlockConfiguration, got {S3Fidelity.Repr((error.Code, error.Message))}");

        var config = new PublicAccessBlockConfiguration
        {
            BlockPublicAcls = true,
            IgnorePublicAcls = true,
            BlockPublicPolicy = false,
            RestrictPublicBuckets = false,
        };
        await s3.PutPublicAccessBlockAsync(new PutPublicAccessBlockRequest { BucketName = bucket, PublicAccessBlockConfiguration = config });
        var got = (await s3.GetPublicAccessBlockAsync(new GetPublicAccessBlockRequest { BucketName = bucket })).PublicAccessBlockConfiguration;
        Ensure(
            got.BlockPublicAcls == config.BlockPublicAcls && got.IgnorePublicAcls == config.IgnorePublicAcls
            && got.BlockPublicPolicy == config.BlockPublicPolicy && got.RestrictPublicBuckets == config.RestrictPublicBuckets,
            $"expected {Describe(config)}, got {Describe(got)}");

        // deleting the config restores the not-found boundary
        await s3.DeletePublicAccessBlockAsync(new DeletePublicAccessBlockRequest { BucketName = bucket });
        var error2 = await S3Fidelity.Error(() => s3.GetPublicAccessBlockAsync(new GetPublicAccessBlockRequest { BucketName = bucket }));
        Ensure((error2.Code, error2.Message) == NoSuchPab, $"expected NoSuchPublicAccessBlockConfiguration, got {S3Fidelity.Repr((error2.Code, error2.Message))}");
    }

    private static string Describe(PublicAccessBlockConfiguration c) =>
        $"{{'BlockPublicAcls': {c.BlockPublicAcls}, 'IgnorePublicAcls': {c.IgnorePublicAcls}, 'BlockPublicPolicy': {c.BlockPublicPolicy}, 'RestrictPublicBuckets': {c.RestrictPublicBuckets}}}";
}
