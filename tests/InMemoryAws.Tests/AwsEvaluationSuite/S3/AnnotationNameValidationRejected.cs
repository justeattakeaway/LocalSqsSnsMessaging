// Translated from behavioral/s3/check_annotation_name_validation_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.annotation-name-validation-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class AnnotationNameValidationRejected
{
    /// <summary>PutObjectAnnotation rejects reserved-prefix and over-length names, leaving the annotation set unchanged.</summary>
    [Test]
    [DisplayName("s3.annotation-name-validation-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var bucket = await S3Fidelity.CreateBucket(ctx, "sim2real-behav-bkt-");
        const string key = "objects/target.txt";
        await s3.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = key, ContentBody = "payload" });

        // Positive control: a compliant annotation name succeeds.
        const string validName = "team.priority";
        await S3Fidelity.PutAnnotation(s3, bucket, key, validName, "P1"u8.ToArray());
        var payload = await S3Fidelity.AnnotationPayload(s3, bucket, key, validName);
        Ensure(payload.SequenceEqual("P1"u8.ToArray()), $"expected positive-control payload b'P1', got {Py.Repr(payload)}");

        // Break precondition #1: annotation names beginning with the reserved "aws" prefix are disallowed.
        const string badName = "aws-reserved-name";
        var (code, message) = await S3Fidelity.ErrorPair(
            () => S3Fidelity.PutAnnotation(s3, bucket, key, badName, "x"u8.ToArray()),
            "expected the call to be rejected, but it SUCCEEDED");
        Ensure(code == "InvalidAnnotation", $"expected InvalidAnnotation, got {code}");
        Ensure(message == "Your annotation name cannot start with 'aws' or 's3'.", $"unexpected message {Py.Repr(message)}");

        // Break precondition #2: annotation names longer than the 512-byte maximum are disallowed.
        var tooLongName = new string('n', 513);
        var (code2, message2) = await S3Fidelity.ErrorPair(
            () => S3Fidelity.PutAnnotation(s3, bucket, key, tooLongName, "x"u8.ToArray()),
            "expected the call to be rejected, but it SUCCEEDED");
        Ensure(code2 == "AnnotationNameTooLong", $"expected AnnotationNameTooLong, got {code2}");
        Ensure(message2 == "Your annotation name is too long.", $"unexpected message {Py.Repr(message2)}");

        // Neither rejected call had any effect: only the valid annotation exists.
        var listed = await S3Fidelity.ListAnnotations(s3, bucket, key);
        var names = S3Fidelity.AnnotationNames(listed);
        Ensure(names.SequenceEqual([validName]), $"expected only {Py.Repr(validName)} to exist, got {Py.Repr(names)}");
        Ensure(listed.AnnotationCount == 1, $"expected AnnotationCount 1, got {listed.AnnotationCount}");
    }
}
