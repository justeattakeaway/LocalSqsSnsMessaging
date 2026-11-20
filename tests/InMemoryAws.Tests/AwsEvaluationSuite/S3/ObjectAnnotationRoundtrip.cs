// Translated from behavioral/s3/check_object_annotation_roundtrip.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.object-annotation-roundtrip</summary>
[Category("AwsEvaluationSuite")]
public sealed class ObjectAnnotationRoundtrip
{
    private const string NoSuchAnnotationMessage = "The specified annotation does not exist.";

    /// <summary>
    /// PutObjectAnnotation entries appear via GetObjectAnnotation/ListObjectAnnotations and are
    /// removable via DeleteObjectAnnotation.
    /// </summary>
    [Test]
    [DisplayName("s3.object-annotation-roundtrip")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var bucket = await S3Fidelity.CreateBucket(ctx, "sim2real-behav-bkt-");
        const string key = "annotated.txt";
        await s3.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = key, ContentBody = "base-object" });
        await S3Fidelity.PutAnnotation(s3, bucket, key, "sim2real-note", "hello-annotation"u8.ToArray());

        var payload = await S3Fidelity.AnnotationPayload(s3, bucket, key, "sim2real-note");
        Ensure(payload.SequenceEqual("hello-annotation"u8.ToArray()), $"expected b'hello-annotation', got {Py.Repr(payload)}");

        var listed = await S3Fidelity.ListAnnotations(s3, bucket, key);
        var names = (listed.Annotations ?? []).Select(a => a.AnnotationName).ToHashSet();
        Ensure(names.Contains("sim2real-note"), $"expected annotation in ListObjectAnnotations, got {Py.Repr(names.ToList())}");
        Ensure(listed.AnnotationCount == 1, $"expected AnnotationCount == 1, got {listed.AnnotationCount}");

        await s3.DeleteObjectAnnotationAsync(new DeleteObjectAnnotationRequest { BucketName = bucket, Key = key, AnnotationName = "sim2real-note" });
        var (code, message) = await S3Fidelity.ErrorPair(() => S3Fidelity.AnnotationPayload(s3, bucket, key, "sim2real-note"), "expected a ClientError, but the call SUCCEEDED");
        Ensure(code == "NoSuchAnnotation", $"expected NoSuchAnnotation after delete, got {code}");
        Ensure(message == NoSuchAnnotationMessage, $"unexpected NoSuchAnnotation message: {Py.Repr(message)}");

        var listed2 = await S3Fidelity.ListAnnotations(s3, bucket, key);
        var names2 = (listed2.Annotations ?? []).Select(a => a.AnnotationName).ToHashSet();
        Ensure(!names2.Contains("sim2real-note"), $"expected annotation gone from list, got {Py.Repr(names2.ToList())}");
        Ensure(listed2.AnnotationCount == 0, $"expected AnnotationCount == 0, got {listed2.AnnotationCount}");
    }
}
