// Translated from behavioral/s3/check_list_object_annotations_max_annotation_results_over_one_thousand_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit abd0811).
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.list-object-annotations-max-annotation-results-over-one-thousand-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class ListObjectAnnotationsMaxAnnotationResultsOverOneThousandRejected
{
    private const string Check = "list-object-annotations-max-annotation-results-over-one-thousand-rejected";
    private const int MaxAllowed = 1000;

    /// <summary>
    /// ListObjectAnnotations accepts MaxAnnotationResults up to 1,000 but rejects 1,001 with
    /// InvalidArgument, leaving the listing otherwise unaffected.
    /// </summary>
    [Test]
    [DisplayName("s3.list-object-annotations-max-annotation-results-over-one-thousand-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var bucket = await S3Fidelity.CreateTaggedBucket(ctx, "sim2real", Check, "sim2real-behav-loamr-");
        const string key = "annotated-object.bin";
        await s3.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = key, ContentBody = "payload" });
        await S3Fidelity.PutAnnotation(s3, bucket, key, "boundary", "v"u8.ToArray());

        var control = await S3Fidelity.ListAnnotations(s3, bucket, key, max: MaxAllowed);
        Ensure(
            S3Fidelity.AnnotationNames(control).SequenceEqual(["boundary"]),
            $"MaxAnnotationResults={MaxAllowed} must be accepted and list the one annotation written, got {Py.Repr(S3Fidelity.AnnotationNames(control))}");

        var (code, message) = await S3Fidelity.ErrorPair(
            () => S3Fidelity.ListAnnotations(s3, bucket, key, max: MaxAllowed + 1),
            "expected a ClientError, the call succeeded");
        Ensure(
            code == "InvalidArgument",
            $"expected InvalidArgument for MaxAnnotationResults={MaxAllowed + 1}, got {Py.Repr(code)}: {Py.Repr(message)}");
        Ensure(
            message == $"Argument max-annotation-results must be an integer between 1 and {MaxAllowed}",
            $"unexpected InvalidArgument message for an over-range MaxAnnotationResults: {Py.Repr(message)}");

        var after = await S3Fidelity.ListAnnotations(s3, bucket, key, max: MaxAllowed);
        Ensure(
            S3Fidelity.AnnotationNames(after).SequenceEqual(["boundary"]),
            $"the rejected over-range request must not have changed the object's annotation list, got {Py.Repr(S3Fidelity.AnnotationNames(after))}");
    }
}
