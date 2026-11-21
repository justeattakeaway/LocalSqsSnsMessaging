// Translated from behavioral/s3/check_object_annotation_nonexistent_version_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit abd0811).
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.object-annotation-nonexistent-version-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class ObjectAnnotationNonexistentVersionRejected
{
    private const string Check = "object-annotation-nonexistent-version-rejected";

    private static readonly (string? Code, string? Message) NoSuchVersion = ("NoSuchVersion", "The specified version does not exist.");

    /// <summary>
    /// A VersionId that once existed but was permanently deleted is refused NoSuchVersion by
    /// every annotation operation (Put, Get, List, Delete), not treated as the current version or
    /// as a missing annotation.
    /// </summary>
    [Test]
    [DisplayName("s3.object-annotation-nonexistent-version-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var bucket = await S3Fidelity.CreateTaggedBucket(ctx, "sim2real", Check, "sim2real-behav-annnover-");
        await S3Fidelity.SetVersioning(s3, bucket, VersionStatus.Enabled);

        const string key = "reports/probe.txt";
        const string annotationName = "review-note";
        var v1 = (await s3.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = key, ContentBody = "version one" })).VersionId;
        await S3Fidelity.PutAnnotation(s3, bucket, key, annotationName, "on version one"u8.ToArray());

        // Permanently remove v1: a DeleteObject naming a VersionId erases that version outright,
        // leaving no object behind it (no new version, no delete marker).
        await s3.DeleteObjectAsync(new DeleteObjectRequest { BucketName = bucket, Key = key, VersionId = v1 });

        var putErr = await ExpectErrorCode(() => s3.PutObjectAnnotationAsync(new PutObjectAnnotationRequest
        {
            BucketName = bucket,
            Key = key,
            AnnotationName = annotationName,
            AnnotationPayload = new MemoryStream("new"u8.ToArray()),
            VersionId = v1,
        }));
        Ensure(putErr == NoSuchVersion, $"PutObjectAnnotation on a deleted version: expected {S3Fidelity.Repr(NoSuchVersion)}, got {S3Fidelity.Repr(putErr)}");

        var getErr = await ExpectErrorCode(async () =>
        {
            using var response = await s3.GetObjectAnnotationAsync(new GetObjectAnnotationRequest
            {
                BucketName = bucket,
                Key = key,
                AnnotationName = annotationName,
                VersionId = v1,
            });
        });
        Ensure(getErr == NoSuchVersion, $"GetObjectAnnotation on a deleted version: expected {S3Fidelity.Repr(NoSuchVersion)}, got {S3Fidelity.Repr(getErr)}");

        var listErr = await ExpectErrorCode(() => s3.ListObjectAnnotationsAsync(new ListObjectAnnotationsRequest
        {
            BucketName = bucket,
            Key = key,
            VersionId = v1,
        }));
        Ensure(listErr == NoSuchVersion, $"ListObjectAnnotations on a deleted version: expected {S3Fidelity.Repr(NoSuchVersion)}, got {S3Fidelity.Repr(listErr)}");

        var deleteErr = await ExpectErrorCode(() => s3.DeleteObjectAnnotationAsync(new DeleteObjectAnnotationRequest
        {
            BucketName = bucket,
            Key = key,
            AnnotationName = annotationName,
            VersionId = v1,
        }));
        Ensure(deleteErr == NoSuchVersion, $"DeleteObjectAnnotation on a deleted version: expected {S3Fidelity.Repr(NoSuchVersion)}, got {S3Fidelity.Repr(deleteErr)}");

        // Positive control: a fresh version in the same key still takes annotations normally.
        var v2 = (await s3.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = key, ContentBody = "version two" })).VersionId;
        var put2 = await S3Fidelity.PutAnnotation(s3, bucket, key, annotationName, "on version two"u8.ToArray());
        Ensure(
            put2.ObjectVersionId == v2,
            $"expected the control annotation to attach to the current version {Py.Repr(v2)}, got {Py.Repr(put2.ObjectVersionId)}");
        var got2 = await S3Fidelity.AnnotationPayload(s3, bucket, key, annotationName);
        Ensure(got2.SequenceEqual("on version two"u8.ToArray()), "expected the control's annotation to read back the payload it just wrote");
    }

    /// <summary>
    /// <c>_expect_error_code(fn)</c>: the botocore (code, message) of a call AWS refuses; an unexpected
    /// success fails the check.
    /// </summary>
    private static async Task<(string? Code, string? Message)> ExpectErrorCode(Func<Task> call)
    {
        var error = await FidelityContext.CaptureError(call)
            ?? throw Fail("expected the call to be rejected, but it succeeded");
        return (error.Code, error.Message ?? "");
    }
}
