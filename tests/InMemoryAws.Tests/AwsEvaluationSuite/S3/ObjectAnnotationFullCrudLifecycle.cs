// Translated from behavioral/s3/check_object_annotation_full_crud_lifecycle.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.object-annotation-full-crud-lifecycle</summary>
[Category("AwsEvaluationSuite")]
public sealed class ObjectAnnotationFullCrudLifecycle
{
    private const string NoSuchAnnotationMessage = "The specified annotation does not exist.";

    /// <summary>
    /// PutObjectAnnotation creates a named annotation visible via Get/List; a second Put to the same name
    /// overwrites in place rather than appending, a distinct name is additive, and Delete removes each
    /// name independently, leaving the rest intact until none remain.
    /// </summary>
    [Test]
    [DisplayName("s3.object-annotation-full-crud-lifecycle")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var bucket = await S3Fidelity.CreateBucket(ctx, "sim2real-annot-");
        const string key = "annotated-object.json";
        await s3.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = key, ContentBody = "{\"source\":\"sensor\"}" });
        ctx.OnCleanup(() => s3.DeleteObjectAsync(bucket, key));

        const string nameA = "ml-inference-label";
        var payload = "{\"label\":\"cat\",\"confidence\":0.97}"u8.ToArray();
        await S3Fidelity.PutAnnotation(s3, bucket, key, nameA, payload);
        var gotPayload = await S3Fidelity.AnnotationPayload(s3, bucket, key, nameA);
        Ensure(gotPayload.SequenceEqual(payload), $"expected payload {Py.Repr(payload)}, got {Py.Repr(gotPayload)}");
        var listed = await S3Fidelity.ListAnnotations(s3, bucket, key);
        var names = S3Fidelity.AnnotationNames(listed);
        Ensure(names.Contains(nameA), $"expected {Py.Repr(nameA)} in {Py.Repr(names)}");
        Ensure(listed.AnnotationCount == 1, $"expected AnnotationCount 1, got {listed.AnnotationCount}");

        // A second Put under the same name overwrites in place; it must not create a second entry.
        var overwritePayload = "{\"label\":\"dog\",\"confidence\":0.61}"u8.ToArray();
        await S3Fidelity.PutAnnotation(s3, bucket, key, nameA, overwritePayload);
        var gotOverwritePayload = await S3Fidelity.AnnotationPayload(s3, bucket, key, nameA);
        Ensure(
            gotOverwritePayload.SequenceEqual(overwritePayload),
            $"expected overwrite to read back {Py.Repr(overwritePayload)}, got {Py.Repr(gotOverwritePayload)}");
        var listedOverwrite = await S3Fidelity.ListAnnotations(s3, bucket, key);
        Ensure(
            listedOverwrite.AnnotationCount == 1,
            $"overwriting an existing name must not add an entry, got AnnotationCount {listedOverwrite.AnnotationCount}");

        // A distinct name is additive, not a replacement of the first.
        const string nameB = "ml-inference-score";
        await S3Fidelity.PutAnnotation(s3, bucket, key, nameB, "0.61"u8.ToArray());
        var listedBoth = await S3Fidelity.ListAnnotations(s3, bucket, key);
        var namesBoth = S3Fidelity.AnnotationNames(listedBoth).Order(StringComparer.Ordinal).ToList();
        Ensure(namesBoth.SequenceEqual(new[] { nameA, nameB }.Order(StringComparer.Ordinal)), $"expected both names present, got {Py.Repr(namesBoth)}");
        Ensure(listedBoth.AnnotationCount == 2, $"expected AnnotationCount 2, got {listedBoth.AnnotationCount}");

        // Deleting one name must leave the other untouched.
        await s3.DeleteObjectAnnotationAsync(new DeleteObjectAnnotationRequest { BucketName = bucket, Key = key, AnnotationName = nameA });
        var (code, message) = await S3Fidelity.ErrorPair(() => S3Fidelity.AnnotationPayload(s3, bucket, key, nameA));
        Ensure(code == "NoSuchAnnotation", $"expected NoSuchAnnotation after delete, got {code}");
        Ensure(message == NoSuchAnnotationMessage, $"unexpected NoSuchAnnotation message: {Py.Repr(message)}");
        var listedOneLeft = await S3Fidelity.ListAnnotations(s3, bucket, key);
        var namesOneLeft = S3Fidelity.AnnotationNames(listedOneLeft);
        Ensure(namesOneLeft.SequenceEqual([nameB]), $"expected only {Py.Repr(nameB)} to remain, got {Py.Repr(namesOneLeft)}");
        Ensure(
            listedOneLeft.AnnotationCount == 1,
            $"expected AnnotationCount 1 after deleting one of two, got {listedOneLeft.AnnotationCount}");

        // Delete the remaining annotation; the object returns to having none.
        await s3.DeleteObjectAnnotationAsync(new DeleteObjectAnnotationRequest { BucketName = bucket, Key = key, AnnotationName = nameB });
        var listedAfter = await S3Fidelity.ListAnnotations(s3, bucket, key);
        Ensure(listedAfter.AnnotationCount == 0, $"expected AnnotationCount 0, got {listedAfter.AnnotationCount}");
        var namesAfter = S3Fidelity.AnnotationNames(listedAfter);
        Ensure(!namesAfter.Contains(nameB), $"annotation still listed after delete: {Py.Repr(namesAfter)}");
    }
}
