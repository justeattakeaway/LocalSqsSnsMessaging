// Translated from behavioral/s3/check_object_annotation_validation_errors_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.object-annotation-validation-errors-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class ObjectAnnotationValidationErrorsRejected
{
    private const string NoSuchAnnotationMessage = "The specified annotation does not exist.";
    private const string InvalidAnnotationMessage = "Your annotation name cannot start with 'aws' or 's3'.";
    private const string NameTooLongMessage = "Your annotation name is too long.";
    private const string LimitExceededMessage = "The object annotation count (1001) exceeds the limit (1000).";

    /// <summary>
    /// PutObjectAnnotation rejects a too-long name (AnnotationNameTooLong), a reserved prefix
    /// (InvalidAnnotation) and the annotation past the per-version cap (AnnotationLimitExceeded) with
    /// exact messages and stores none of them, while a valid annotation round-trips its payload.
    /// </summary>
    [Test]
    [DisplayName("s3.object-annotation-validation-errors-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var bucket = await S3Fidelity.CreateBucket(ctx, "sim2real-behav-");
        const string key = "annotated-object.bin";
        await s3.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = key, ContentBody = "parent-object-body" });

        // --- Positive control: a valid annotation is created and round-trips exactly. ---
        const string validName = "pipeline.stage-1_result";
        var validPayload = "{\"label\": \"cat\", \"confidence\": 0.97}"u8.ToArray();
        await S3Fidelity.PutAnnotation(s3, bucket, key, validName, validPayload);
        Ensure((await S3Fidelity.AnnotationPayload(s3, bucket, key, validName)).SequenceEqual(validPayload), "assert fetched['AnnotationPayload'].read() == valid_payload");

        // --- AnnotationNameTooLong: name over 512 bytes is rejected. ---
        var tooLongName = new string('n', 513);
        var (code, message) = await S3Fidelity.ErrorPair(() => S3Fidelity.PutAnnotation(s3, bucket, key, tooLongName, "x"u8.ToArray()));
        Ensure(code == "AnnotationNameTooLong", $"expected AnnotationNameTooLong, got {Py.Repr(code)}");
        Ensure(message == NameTooLongMessage, $"unexpected AnnotationNameTooLong message: {Py.Repr(message)}");

        // GetObjectAnnotation validates the name (same rule as PutObjectAnnotation) before it
        // would even look up whether an annotation exists, so an over-length name is rejected
        // with the same validation error rather than NoSuchAnnotation -- either way, no
        // annotation was ever created under this name.
        var (notFoundCode, notFoundMessage) = await S3Fidelity.ErrorPair(() => S3Fidelity.AnnotationPayload(s3, bucket, key, tooLongName));
        Ensure(
            (notFoundCode, notFoundMessage) == (code, message),
            "expected the too-long name to be rejected identically by GetObjectAnnotation " +
            $"({Py.Repr(code)}, {Py.Repr(message)}), got ({Py.Repr(notFoundCode)}, {Py.Repr(notFoundMessage)})");

        // --- InvalidAnnotationName: reserved "aws"/"s3" prefixes are rejected. ---
        const string reservedName = "aws-reserved-annotation";
        (code, message) = await S3Fidelity.ErrorPair(() => S3Fidelity.PutAnnotation(s3, bucket, key, reservedName, "x"u8.ToArray()));
        Ensure(code == "InvalidAnnotation", $"expected InvalidAnnotation, got {Py.Repr(code)}");
        Ensure(message == InvalidAnnotationMessage, $"unexpected InvalidAnnotation message: {Py.Repr(message)}");

        // Same name-validation-before-lookup behavior as above: the reserved-prefix name is
        // rejected identically by GetObjectAnnotation, confirming no annotation was created.
        (notFoundCode, notFoundMessage) = await S3Fidelity.ErrorPair(() => S3Fidelity.AnnotationPayload(s3, bucket, key, reservedName));
        Ensure(
            (notFoundCode, notFoundMessage) == (code, message),
            "expected the reserved-prefix name to be rejected identically by GetObjectAnnotation " +
            $"({Py.Repr(code)}, {Py.Repr(message)}), got ({Py.Repr(notFoundCode)}, {Py.Repr(notFoundMessage)})");

        // --- AnnotationLimitExceeded: an object version caps out at 1000 annotations. ---
        // We already created 1 (valid_name); fill up to the 1000-annotation ceiling concurrently.
        const int remaining = 1000 - 1;
        var names = Enumerable.Range(0, remaining).Select(i => $"bulk-annotation-{i:0000}");

        // Every error encountered while filling is retried with backoff, as the check does: none of
        // them is the terminal AnnotationLimitExceeded rejection under test below.
        await S3Fidelity.RunPool(ctx, 24, names, async name =>
        {
            for (var attempt = 0; ; attempt++)
            {
                var error = await FidelityContext.CaptureError(() => S3Fidelity.PutAnnotation(s3, bucket, key, name, "1"u8.ToArray()));
                if (error is null)
                {
                    return true;
                }

                if (attempt >= 14)
                {
                    throw error.Exception;
                }

                await FidelityTime.Delay(TimeSpan.FromSeconds(Math.Min(0.25 * Math.Pow(1.6, attempt + 1), 4.0)));
            }
        });

        (code, message) = await S3Fidelity.ErrorPair(() => S3Fidelity.PutAnnotation(s3, bucket, key, "one-too-many", "x"u8.ToArray()));
        Ensure(code == "AnnotationLimitExceeded", $"expected AnnotationLimitExceeded, got {Py.Repr(code)}");
        Ensure(message == LimitExceededMessage, $"unexpected AnnotationLimitExceeded message: {Py.Repr(message)}");

        (notFoundCode, notFoundMessage) = await S3Fidelity.ErrorPair(() => S3Fidelity.AnnotationPayload(s3, bucket, key, "one-too-many"));
        Ensure(notFoundCode == "NoSuchAnnotation", $"expected NoSuchAnnotation, got {Py.Repr(notFoundCode)}");
        Ensure(notFoundMessage == NoSuchAnnotationMessage, $"unexpected NoSuchAnnotation message: {Py.Repr(notFoundMessage)}");
    }
}
