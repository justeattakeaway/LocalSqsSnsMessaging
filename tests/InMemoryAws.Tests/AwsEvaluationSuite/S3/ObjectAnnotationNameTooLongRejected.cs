// Translated from behavioral/s3/check_object_annotation_name_too_long_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.object-annotation-name-too-long-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class ObjectAnnotationNameTooLongRejected
{
    /// <summary>
    /// PutObjectAnnotation with an AnnotationName over the 512-byte maximum is rejected with
    /// AnnotationNameTooLong rather than truncated or accepted, leaving the annotations unchanged.
    /// </summary>
    [Test]
    [DisplayName("s3.object-annotation-name-too-long-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var bucket = await S3Fidelity.CreateBucket(ctx, "sim2real-behav-bkt-");
        const string key = "annotation-guard.txt";
        await s3.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = key, ContentBody = "data" });

        // Positive control: a compliant annotation name is accepted, so the account and
        // credentials are otherwise healthy for this operation.
        const string validName = "sim2real-guard";
        await S3Fidelity.PutAnnotation(s3, bucket, key, validName, "ok"u8.ToArray());

        var tooLongName = "sim2real-" + new string('x', 2048);
        var (code, message) = await S3Fidelity.ErrorPair(
            () => S3Fidelity.PutAnnotation(s3, bucket, key, tooLongName, "payload"u8.ToArray()),
            "expected the call to be rejected, but it SUCCEEDED");
        Ensure(code == "AnnotationNameTooLong", $"expected AnnotationNameTooLong, got {Py.Repr(code)}");
        Ensure(message == "Your annotation name is too long.", $"expected the exact AnnotationNameTooLong message, got {Py.Repr(message)}");

        // The rejected call must not have created an annotation: only the valid control exists.
        var names = S3Fidelity.AnnotationNames(await S3Fidelity.ListAnnotations(s3, bucket, key));
        Ensure(names.SequenceEqual([validName]), $"expected only {Py.Repr(validName)} to exist, got {Py.Repr(names)}");
    }
}
