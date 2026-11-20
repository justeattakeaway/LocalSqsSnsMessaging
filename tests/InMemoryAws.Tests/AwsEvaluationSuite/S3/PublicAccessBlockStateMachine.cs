// Translated from behavioral/s3/check_public_access_block_state_machine.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.public-access-block-state-machine</summary>
[Category("AwsEvaluationSuite")]
public sealed class PublicAccessBlockStateMachine
{
    /// <summary>
    /// Block Public Access is all-on by default, is fully settable, and after
    /// DeletePublicAccessBlock Get yields NoSuchPublicAccessBlockConfiguration.
    /// </summary>
    [Test]
    [DisplayName("s3.public-access-block-state-machine")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var name = await S3Fidelity.CreateBucket(ctx, "sim2real-behav-s3fid-");

        var defaultCfg = (await s3.GetPublicAccessBlockAsync(new GetPublicAccessBlockRequest { BucketName = name })).PublicAccessBlockConfiguration;
        Ensure(
            Fields(defaultCfg).All(f => f == true),
            $"new buckets must default to Block Public Access all-on, got {Describe(defaultCfg)}");

        await s3.PutPublicAccessBlockAsync(new PutPublicAccessBlockRequest
        {
            BucketName = name,
            PublicAccessBlockConfiguration = new PublicAccessBlockConfiguration
            {
                BlockPublicAcls = false,
                IgnorePublicAcls = false,
                BlockPublicPolicy = false,
                RestrictPublicBuckets = false,
            },
        });
        var off = (await s3.GetPublicAccessBlockAsync(new GetPublicAccessBlockRequest { BucketName = name })).PublicAccessBlockConfiguration;
        Ensure(Fields(off).All(f => f == false), $"all-off config not persisted: {Describe(off)}");

        await s3.PutPublicAccessBlockAsync(new PutPublicAccessBlockRequest
        {
            BucketName = name,
            PublicAccessBlockConfiguration = new PublicAccessBlockConfiguration
            {
                BlockPublicAcls = true,
                IgnorePublicAcls = false,
                BlockPublicPolicy = true,
                RestrictPublicBuckets = false,
            },
        });
        var mixed = (await s3.GetPublicAccessBlockAsync(new GetPublicAccessBlockRequest { BucketName = name })).PublicAccessBlockConfiguration;
        Ensure(mixed.BlockPublicAcls == true && mixed.IgnorePublicAcls == false, $"mixed config not persisted: {Describe(mixed)}");
        Ensure(mixed.BlockPublicPolicy == true && mixed.RestrictPublicBuckets == false, $"mixed config not persisted: {Describe(mixed)}");

        await s3.DeletePublicAccessBlockAsync(new DeletePublicAccessBlockRequest { BucketName = name });
        var error = await S3Fidelity.Error(
            () => s3.GetPublicAccessBlockAsync(new GetPublicAccessBlockRequest { BucketName = name }),
            "expected the call to be rejected, but it SUCCEEDED");
        Ensure(
            (error.Code, error.Message) == ("NoSuchPublicAccessBlockConfiguration", "The public access block configuration was not found"),
            $"expected NoSuchPublicAccessBlockConfiguration, got {S3Fidelity.Repr((error.Code, error.Message))}");
    }

    private static bool?[] Fields(PublicAccessBlockConfiguration c) =>
        [c.BlockPublicAcls, c.IgnorePublicAcls, c.BlockPublicPolicy, c.RestrictPublicBuckets];

    private static string Describe(PublicAccessBlockConfiguration c) =>
        $"{{'BlockPublicAcls': {c.BlockPublicAcls}, 'IgnorePublicAcls': {c.IgnorePublicAcls}, 'BlockPublicPolicy': {c.BlockPublicPolicy}, 'RestrictPublicBuckets': {c.RestrictPublicBuckets}}}";
}
