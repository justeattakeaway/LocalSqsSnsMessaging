// Translated from behavioral/s3/check_bucket_encryption_config_lifecycle.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.bucket-encryption-config-lifecycle</summary>
[Category("AwsEvaluationSuite")]
public sealed class BucketEncryptionConfigLifecycle
{
    /// <summary>
    /// Bucket default encryption can be set, read back exactly, and removed -- reverting
    /// the bucket to the base SSE-S3 default-encryption configuration AWS applies to every
    /// bucket.
    /// </summary>
    [Test]
    [DisplayName("s3.bucket-encryption-config-lifecycle")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var bucket = await S3Fidelity.CreateBucket(ctx, "sim2real-sse-");

        // S3 applies a base SSE-S3 (AES256) default encryption configuration to every new
        // bucket, so GetBucketEncryption succeeds from the start.
        var defaultAlgo = (await FirstRule(s3, bucket)).ServerSideEncryptionByDefault.ServerSideEncryptionAlgorithm?.Value;
        Ensure(defaultAlgo == "AES256", $"expected automatic SSE-S3 default, got {Py.Repr(defaultAlgo)}");

        // Explicitly set a different configuration (SSE-KMS via the AWS managed key) so we
        // can distinguish "the automatic default" from "a configuration we set ourselves".
        await PutDefault(s3, bucket, ServerSideEncryptionMethod.AWSKMS);
        var got = (await s3.GetBucketEncryptionAsync(new GetBucketEncryptionRequest { BucketName = bucket })).ServerSideEncryptionConfiguration;
        Ensure(got.ServerSideEncryptionRules.Count == 1, $"expected exactly one rule, got {got.ServerSideEncryptionRules.Count}");
        var kmsAlgo = got.ServerSideEncryptionRules[0].ServerSideEncryptionByDefault.ServerSideEncryptionAlgorithm?.Value;
        Ensure(kmsAlgo == "aws:kms", $"expected aws:kms after PutBucketEncryption, got {Py.Repr(kmsAlgo)}");

        // Removing the explicit configuration reverts the bucket to the automatic SSE-S3
        // default rather than leaving it with no encryption configuration at all.
        await s3.DeleteBucketEncryptionAsync(new DeleteBucketEncryptionRequest { BucketName = bucket });
        var revertedAlgo = (await FirstRule(s3, bucket)).ServerSideEncryptionByDefault.ServerSideEncryptionAlgorithm?.Value;
        Ensure(revertedAlgo == "AES256", $"expected revert to SSE-S3 default, got {Py.Repr(revertedAlgo)}");
    }

    private static async Task<ServerSideEncryptionRule> FirstRule(AmazonS3Client s3, string bucket) =>
        (await s3.GetBucketEncryptionAsync(new GetBucketEncryptionRequest { BucketName = bucket })).ServerSideEncryptionConfiguration.ServerSideEncryptionRules[0];

    private static Task PutDefault(AmazonS3Client s3, string bucket, ServerSideEncryptionMethod algorithm, bool? bucketKeyEnabled = null) =>
        s3.PutBucketEncryptionAsync(new PutBucketEncryptionRequest
        {
            BucketName = bucket,
            ServerSideEncryptionConfiguration = new ServerSideEncryptionConfiguration
            {
                ServerSideEncryptionRules =
                [
                    new ServerSideEncryptionRule
                    {
                        ServerSideEncryptionByDefault = new ServerSideEncryptionByDefault { ServerSideEncryptionAlgorithm = algorithm },
                        BucketKeyEnabled = bucketKeyEnabled,
                    },
                ],
            },
        });
}
