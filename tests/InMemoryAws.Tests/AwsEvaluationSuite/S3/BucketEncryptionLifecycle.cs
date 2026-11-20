// Translated from behavioral/s3/check_bucket_encryption_lifecycle.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.bucket-encryption-lifecycle</summary>
[Category("AwsEvaluationSuite")]
public sealed class BucketEncryptionLifecycle
{
    /// <summary>
    /// PutBucketEncryption -> GetBucketEncryption roundtrips a KMS config; after
    /// DeleteBucketEncryption the bucket reverts to the platform default (SSE-S3/AES256)
    /// rather than retaining the KMS configuration we set.
    /// </summary>
    [Test]
    [DisplayName("s3.bucket-encryption-lifecycle")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var name = await S3Fidelity.CreateBucket(ctx, "sim2real-behav-enc-");
        await PutDefault(s3, name, ServerSideEncryptionMethod.AWSKMS, bucketKeyEnabled: true);
        var rule = await FirstRule(s3, name);
        Ensure(rule.ServerSideEncryptionByDefault.ServerSideEncryptionAlgorithm?.Value == "aws:kms", Py.Repr(rule.ServerSideEncryptionByDefault.ServerSideEncryptionAlgorithm?.Value));
        Ensure(rule.BucketKeyEnabled == true, Py.Repr(rule.BucketKeyEnabled));
        await s3.DeleteBucketEncryptionAsync(new DeleteBucketEncryptionRequest { BucketName = name });

        // Since January 2023 every S3 bucket carries a base SSE-S3 (AES256) default
        // encryption configuration, so DeleteBucketEncryption reverts to that baseline
        // rather than leaving the bucket with none -- Get no longer raises
        // ServerSideEncryptionConfigurationNotFoundError.
        var algo = (await FirstRule(s3, name)).ServerSideEncryptionByDefault.ServerSideEncryptionAlgorithm?.Value;
        Ensure(algo == "AES256", $"expected revert to the AES256 baseline default, got {algo}");
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
