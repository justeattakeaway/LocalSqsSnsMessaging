// Translated from behavioral/s3/check_object_annotation_not_found_errors.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.object-annotation-not-found-errors</summary>
[Category("AwsEvaluationSuite")]
public sealed class ObjectAnnotationNotFoundErrors
{
    private const string NoSuchAnnotationMessage = "The specified annotation does not exist.";

    /// <summary>
    /// GetObjectAnnotation returns NoSuchBucket, NoSuchKey, or NoSuchAnnotation depending on which
    /// resource in the chain (bucket / object / annotation) is missing; once the chain is complete, the
    /// identical call succeeds.
    /// </summary>
    [Test]
    [DisplayName("s3.object-annotation-not-found-errors")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var fakeBucket = FidelityContext.UniqueName("sim2real-behav-annot-nobkt-");
        var (code1, message1) = await S3Fidelity.ErrorPair(() => S3Fidelity.AnnotationPayload(s3, fakeBucket, "whatever.txt", "note"));
        Ensure(code1 == "NoSuchBucket", $"expected NoSuchBucket, got {code1}");
        Ensure(message1 == "The specified bucket does not exist", $"unexpected NoSuchBucket message: {Py.Repr(message1)}");

        var bucket = await S3Fidelity.CreateBucket(ctx, "sim2real-behav-bkt-");
        var (code2, message2) = await S3Fidelity.ErrorPair(() => S3Fidelity.AnnotationPayload(s3, bucket, "never-created.txt", "note"));
        Ensure(code2 == "NoSuchKey", $"expected NoSuchKey, got {code2}");
        Ensure(message2 == "The specified key does not exist.", $"unexpected NoSuchKey message: {Py.Repr(message2)}");

        const string key = "annot-get-target.txt";
        await s3.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = key, ContentBody = "data" });
        const string annotationName = "never-set";
        var (code3, message3) = await S3Fidelity.ErrorPair(() => S3Fidelity.AnnotationPayload(s3, bucket, key, annotationName));
        Ensure(code3 == "NoSuchAnnotation", $"expected NoSuchAnnotation, got {code3}");
        Ensure(message3 == NoSuchAnnotationMessage, $"unexpected NoSuchAnnotation message: {Py.Repr(message3)}");

        // Positive control: once bucket, key and annotation all exist, the identical call
        // (same bucket, key and annotation name) succeeds and returns what was put.
        var payload = "now-set"u8.ToArray();
        await S3Fidelity.PutAnnotation(s3, bucket, key, annotationName, payload);
        var gotPayload = await S3Fidelity.AnnotationPayload(s3, bucket, key, annotationName);
        Ensure(gotPayload.SequenceEqual(payload), $"expected {Py.Repr(payload)} once the chain is complete, got {Py.Repr(gotPayload)}");
    }
}
