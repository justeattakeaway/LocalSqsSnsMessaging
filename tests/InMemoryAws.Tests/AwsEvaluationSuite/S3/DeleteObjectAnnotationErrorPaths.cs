// Translated from behavioral/s3/check_delete_object_annotation_error_paths.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.delete-object-annotation-error-paths</summary>
[Category("AwsEvaluationSuite")]
public sealed class DeleteObjectAnnotationErrorPaths
{
    /// <summary>
    /// DeleteObjectAnnotation returns NoSuchBucket for a missing bucket and NoSuchKey for a missing
    /// object, rather than treating either as an already-satisfied delete.
    /// </summary>
    [Test]
    [DisplayName("s3.delete-object-annotation-error-paths")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;

        // 1. Nonexistent bucket -> NoSuchBucket.
        var missingBucket = FidelityContext.UniqueName("sim2real-nobucket-");
        var codeBucket = await S3Fidelity.ErrorPair(() => Delete(s3, missingBucket, "whatever", "ann"), "expected a ClientError, but the call succeeded");
        Ensure(
            codeBucket == ("NoSuchBucket", "The specified bucket does not exist"),
            $"expected NoSuchBucket with AWS's exact message, got {S3Fidelity.Repr(codeBucket)}");

        var bucket = await S3Fidelity.CreateBucket(ctx, "sim2real-behav-");

        // 2. Existing bucket, missing object -> NoSuchKey.
        var codeKey = await S3Fidelity.ErrorPair(() => Delete(s3, bucket, "does-not-exist.bin", "ann"), "expected a ClientError, but the call succeeded");
        Ensure(
            codeKey == ("NoSuchKey", "The specified key does not exist."),
            $"expected NoSuchKey with AWS's exact message, got {S3Fidelity.Repr(codeKey)}");

        // Sanity: deleting a real annotation on a real object succeeds.
        const string key = "annotated-object.bin";
        await s3.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = key, ContentBody = "payload" });
        ctx.OnCleanup(() => s3.DeleteObjectAsync(bucket, key));
        await S3Fidelity.PutAnnotation(s3, bucket, key, "ann", "1"u8.ToArray());
        await Delete(s3, bucket, key, "ann");
        var codeGone = await S3Fidelity.ErrorPair(() => S3Fidelity.AnnotationPayload(s3, bucket, key, "ann"), "expected a ClientError, but the call succeeded");
        Ensure(
            codeGone == ("NoSuchAnnotation", "The specified annotation does not exist."),
            $"expected NoSuchAnnotation with AWS's exact message, got {S3Fidelity.Repr(codeGone)}");
    }

    private static Task<DeleteObjectAnnotationResponse> Delete(IAmazonS3 s3, string bucket, string key, string name) =>
        s3.DeleteObjectAnnotationAsync(new DeleteObjectAnnotationRequest { BucketName = bucket, Key = key, AnnotationName = name });
}
