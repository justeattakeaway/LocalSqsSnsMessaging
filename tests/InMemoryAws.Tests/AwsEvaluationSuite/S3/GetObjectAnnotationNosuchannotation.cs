// Translated from behavioral/s3/check_get_object_annotation_nosuchannotation.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.get-object-annotation-nosuchannotation</summary>
[Category("AwsEvaluationSuite")]
public sealed class GetObjectAnnotationNosuchannotation
{
    /// <summary>
    /// GetObjectAnnotation returns NoSuchAnnotation when the named annotation does not exist on an
    /// otherwise perfectly valid object, rather than an empty payload or a generic error.
    /// </summary>
    [Test]
    [DisplayName("s3.get-object-annotation-nosuchannotation")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var bucket = await S3Fidelity.CreateBucket(ctx, "sim2real-behav-");
        const string key = "annotated-object.bin";
        await s3.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = key, ContentBody = "payload" });
        ctx.OnCleanup(() => s3.DeleteObjectAsync(bucket, key));

        // A real annotation exists, but under a different name.
        await S3Fidelity.PutAnnotation(s3, bucket, key, "present-annotation", "hi"u8.ToArray());

        // Sanity: fetching the real one works.
        var payload = await S3Fidelity.AnnotationPayload(s3, bucket, key, "present-annotation");
        Ensure(payload.SequenceEqual("hi"u8.ToArray()), $"positive control: expected annotation payload b'hi', got {Py.Repr(payload)}");

        var err = await S3Fidelity.Error(
            () => S3Fidelity.AnnotationPayload(s3, bucket, key, "absent-annotation"),
            "expected NoSuchAnnotation, but the call SUCCEEDED");
        Ensure(err.Code == "NoSuchAnnotation", $"expected NoSuchAnnotation for an annotation name that was never set, got {err}");
        Ensure(err.Message == "The specified annotation does not exist.", err.Message ?? "None");
    }
}
