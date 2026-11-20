// Translated from behavioral/s3/check_copy_object_annotation_exclude_directive_skips_default_copies.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.copy-object-annotation-exclude-directive-skips-default-copies</summary>
[Category("AwsEvaluationSuite")]
public sealed class CopyObjectAnnotationExcludeDirectiveSkipsDefaultCopies
{
    /// <summary>
    /// CopyObject copies the source's annotation byte for byte by default, while
    /// AnnotationDirective=EXCLUDE leaves the copy with none (NoSuchAnnotation); the source keeps it.
    /// </summary>
    [Test]
    [DisplayName("s3.copy-object-annotation-exclude-directive-skips-default-copies")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var bucket = await S3Fidelity.CreateBucket(ctx, "sim2real-behav-anncp-");

        const string srcKey = "src.txt";
        await s3.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = srcKey, ContentBody = "hello" });
        await S3Fidelity.PutAnnotation(s3, bucket, srcKey, "latest-note", "copied annotation"u8.ToArray());

        // Positive control: the default directive (no header at all) propagates the annotation.
        const string copiedKey = "docs/annotated-copied.txt";
        await s3.CopyObjectAsync(new CopyObjectRequest { SourceBucket = bucket, SourceKey = srcKey, DestinationBucket = bucket, DestinationKey = copiedKey });
        var copied = await S3Fidelity.AnnotationPayload(s3, bucket, copiedKey, "latest-note");
        Ensure(copied.SequenceEqual("copied annotation"u8.ToArray()), "default CopyObject must propagate the source's annotation to the copy");

        // Break exactly one thing: ask the copy to EXCLUDE annotations.
        const string excludedKey = "docs/annotated-excluded.txt";
        await s3.CopyObjectAsync(new CopyObjectRequest
        {
            SourceBucket = bucket,
            SourceKey = srcKey,
            DestinationBucket = bucket,
            DestinationKey = excludedKey,
            AnnotationDirective = AnnotationDirective.EXCLUDE,
        });
        var (code, message) = await S3Fidelity.ErrorPair(
            () => S3Fidelity.AnnotationPayload(s3, bucket, excludedKey, "latest-note"),
            "the call succeeded where real AWS rejects it");
        Ensure(code == "NoSuchAnnotation", $"expected EXCLUDE to leave the copy with no 'latest-note' annotation, got {Py.Repr(code)}");
        Ensure(message == "The specified annotation does not exist.", $"NoSuchAnnotation must carry S3's own message; got {Py.Repr(message)}");

        // The source itself is untouched by either copy.
        var sourceStill = await S3Fidelity.AnnotationPayload(s3, bucket, srcKey, "latest-note");
        Ensure(sourceStill.SequenceEqual("copied annotation"u8.ToArray()), "the source object's own annotation must be unaffected by copying it");
    }
}
