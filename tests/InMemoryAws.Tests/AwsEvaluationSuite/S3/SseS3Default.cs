// Translated from behavioral/s3/check_sse_s3_default.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.sse-s3-default</summary>
[Category("AwsEvaluationSuite")]
public sealed class SseS3Default
{
    /// <summary>Objects are encrypted with SSE-S3 (AES256) by default when no bucket-level
    /// encryption configuration has been set -- this is the automatic base default AWS
    /// applies to every bucket, distinct from an explicitly-configured SSE-KMS default.</summary>
    [Test]
    [DisplayName("s3.sse-s3-default")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var bucket = await S3Fidelity.CreateTaggedBucket(ctx, "sim2real-check", "sse-s3-default");
        await s3.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = "sse-default.txt", ContentBody = "encrypted" });
        var head = await s3.GetObjectMetadataAsync(bucket, "sse-default.txt");
        var sse = head.ServerSideEncryptionMethod?.Value;
        Ensure(sse == "AES256", $"expected AES256 (the automatic base default), got {Py.Repr(sse)}");
    }
}
