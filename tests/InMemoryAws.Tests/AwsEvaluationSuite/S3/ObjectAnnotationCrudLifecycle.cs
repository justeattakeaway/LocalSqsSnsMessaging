// Translated from behavioral/s3/check_object_annotation_crud_lifecycle.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.object-annotation-crud-lifecycle</summary>
[Category("AwsEvaluationSuite")]
public sealed class ObjectAnnotationCrudLifecycle
{
    private const string NoSuchAnnotationMessage = "The specified annotation does not exist.";

    /// <summary>Put -> Get -> List -> Delete an object annotation; list reflects the current set exactly.</summary>
    [Test]
    [DisplayName("s3.object-annotation-crud-lifecycle")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var bucket = await S3Fidelity.CreateBucket(ctx, "sim2real-behav-bkt-");
        const string key = "docs/report.json";
        await s3.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = key, ContentBody = "{\"hello\":\"world\"}" });

        const string annName = "project.notes";
        var payload = "first annotation payload"u8.ToArray();
        await S3Fidelity.PutAnnotation(s3, bucket, key, annName, payload);
        var got = await S3Fidelity.GetAnnotation(s3, bucket, key, annName);
        Ensure(got.Payload.SequenceEqual(payload), $"expected payload {Py.Repr(payload)}, got {Py.Repr(got.Payload)}");
        Ensure(got.ContentLength == payload.Length, $"expected ContentLength {payload.Length}, got {got.ContentLength}");

        var listed = await S3Fidelity.ListAnnotations(s3, bucket, key);
        var names = S3Fidelity.AnnotationNames(listed);
        Ensure(names.SequenceEqual([annName]), $"expected only {Py.Repr(annName)}, got {Py.Repr(names)}");
        Ensure(listed.AnnotationCount == 1, $"expected AnnotationCount 1, got {listed.AnnotationCount}");

        // Overwrite in place: PutObjectAnnotation replaces the payload for the same name.
        var newPayload = "updated annotation payload, longer than before"u8.ToArray();
        await S3Fidelity.PutAnnotation(s3, bucket, key, annName, newPayload);
        var got2 = await S3Fidelity.GetAnnotation(s3, bucket, key, annName);
        Ensure(got2.Payload.SequenceEqual(newPayload), $"expected overwritten payload {Py.Repr(newPayload)}, got {Py.Repr(got2.Payload)}");
        Ensure(got2.ContentLength == newPayload.Length, $"expected ContentLength {newPayload.Length} after overwrite, got {got2.ContentLength}");

        // A second, distinct annotation is additive, not a replacement.
        const string otherName = "project.status";
        await S3Fidelity.PutAnnotation(s3, bucket, key, otherName, "open"u8.ToArray());
        var listed2 = await S3Fidelity.ListAnnotations(s3, bucket, key);
        var names2 = S3Fidelity.AnnotationNames(listed2).Order(StringComparer.Ordinal).ToList();
        Ensure(names2.SequenceEqual(new[] { annName, otherName }.Order(StringComparer.Ordinal)), $"expected both annotations, got {Py.Repr(names2)}");
        Ensure(listed2.AnnotationCount == 2, $"expected AnnotationCount 2, got {listed2.AnnotationCount}");

        await s3.DeleteObjectAnnotationAsync(new DeleteObjectAnnotationRequest { BucketName = bucket, Key = key, AnnotationName = annName });
        var listed3 = await S3Fidelity.ListAnnotations(s3, bucket, key);
        var names3 = S3Fidelity.AnnotationNames(listed3);
        Ensure(names3.SequenceEqual([otherName]), $"expected only {Py.Repr(otherName)} to remain, got {Py.Repr(names3)}");
        Ensure(listed3.AnnotationCount == 1, $"expected AnnotationCount 1 after partial delete, got {listed3.AnnotationCount}");

        var (code, message) = await S3Fidelity.ErrorPair(() => S3Fidelity.AnnotationPayload(s3, bucket, key, annName));
        Ensure(code == "NoSuchAnnotation", $"expected NoSuchAnnotation, got {code}");
        Ensure(message == NoSuchAnnotationMessage, $"unexpected NoSuchAnnotation message: {Py.Repr(message)}");
    }
}
