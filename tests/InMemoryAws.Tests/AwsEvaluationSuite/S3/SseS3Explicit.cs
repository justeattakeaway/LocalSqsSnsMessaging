// Translated from behavioral/s3/check_sse_s3_explicit.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.sse-s3-explicit</summary>
[Category("AwsEvaluationSuite")]
public sealed class SseS3Explicit
{
    /// <summary>
    /// On a bucket whose default encryption is aws:kms, a PutObject with an explicit
    /// ServerSideEncryption=AES256 stores the object as AES256, while a plain PutObject stores it as aws:kms:
    /// the per-request header overrides the bucket default.
    /// </summary>
    [Test]
    [DisplayName("s3.sse-s3-explicit")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var bucket = await S3Fidelity.CreateBucket(ctx, "sim2real-behav-bkt-");
        await PutDefault(s3, bucket, ServerSideEncryptionMethod.AWSKMS);

        // No override: the bucket's aws:kms default governs.
        await s3.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = "inherits-default.txt", ContentBody = "inherits-default" });
        var defaultHead = await s3.GetObjectMetadataAsync(bucket, "inherits-default.txt");
        Ensure(
            defaultHead.ServerSideEncryptionMethod?.Value == "aws:kms",
            "expected the bucket's aws:kms default on an object with no override, got " +
            Py.Repr(defaultHead.ServerSideEncryptionMethod?.Value));

        // Explicit AES256 on the SAME kms-default bucket must override it.
        await s3.PutObjectAsync(new PutObjectRequest
        {
            BucketName = bucket,
            Key = "sse-aes.txt",
            ContentBody = "explicit-sse",
            ServerSideEncryptionMethod = ServerSideEncryptionMethod.AES256,
        });
        var head = await s3.GetObjectMetadataAsync(bucket, "sse-aes.txt");
        Ensure(
            head.ServerSideEncryptionMethod?.Value == "AES256",
            "expected an explicit ServerSideEncryption=AES256 request to override the " +
            $"bucket's aws:kms default, got {Py.Repr(head.ServerSideEncryptionMethod?.Value)}");
    }

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
