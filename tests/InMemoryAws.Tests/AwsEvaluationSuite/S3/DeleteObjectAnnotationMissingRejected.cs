// Translated from behavioral/s3/check_delete_object_annotation_missing_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.delete-object-annotation-missing-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class DeleteObjectAnnotationMissingRejected
{
    /// <summary>
    /// DeleteObjectAnnotation on a never-created key fails with NoSuchKey and the key stays absent,
    /// while deleting a real annotation on a real object succeeds and then reads NoSuchAnnotation.
    /// </summary>
    [Test]
    [DisplayName("s3.delete-object-annotation-missing-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var name = await S3Fidelity.CreateBucket(ctx, "sim2real-behav-bkt-");
        const string key = "annotated.txt";
        await s3.PutObjectAsync(new PutObjectRequest { BucketName = name, Key = key, ContentBody = "payload" });
        await S3Fidelity.PutAnnotation(s3, name, key, "sim2real-note", "1"u8.ToArray());

        await Delete(s3, name, key, "sim2real-note");
        var codeGone = await S3Fidelity.ErrorPair(
            () => S3Fidelity.AnnotationPayload(s3, name, key, "sim2real-note"),
            "expected this call to be rejected, but it SUCCEEDED");
        Ensure(
            codeGone == ("NoSuchAnnotation", "The specified annotation does not exist."),
            $"expected the deleted annotation to read back NoSuchAnnotation, got {S3Fidelity.Repr(codeGone)}");

        // Deleting a never-set annotation name on an existing object is an idempotent success, so the
        // must-fail boundary is a missing parent object.
        var code = await S3Fidelity.ErrorPair(
            () => Delete(s3, name, "never-created.txt", "sim2real-note"),
            "expected this call to be rejected, but it SUCCEEDED");
        Ensure(
            code == ("NoSuchKey", "The specified key does not exist."),
            $"expected NoSuchKey deleting an annotation on a nonexistent object, got {S3Fidelity.Repr(code)}");

        // The rejected delete must have had no side effect: the key it named still does not exist.
        // A HEAD response has no body, so botocore fills Message from the HTTP reason phrase.
        var codeHead = await S3Fidelity.HeadErrorPair(
            () => s3.GetObjectMetadataAsync(name, "never-created.txt"),
            "expected this call to be rejected, but it SUCCEEDED");
        Ensure(codeHead == ("404", "Not Found"), $"expected the never-created key to still be absent, got {S3Fidelity.Repr(codeHead)}");
    }

    private static Task<DeleteObjectAnnotationResponse> Delete(IAmazonS3 s3, string bucket, string key, string name) =>
        s3.DeleteObjectAnnotationAsync(new DeleteObjectAnnotationRequest { BucketName = bucket, Key = key, AnnotationName = name });
}
