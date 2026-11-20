// Translated from behavioral/s3/check_list_object_annotations_invalid_prefix.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.list-object-annotations-invalid-prefix</summary>
[Category("AwsEvaluationSuite")]
public sealed class ListObjectAnnotationsInvalidPrefix
{
    /// <summary>
    /// ListObjectAnnotations rejects a malformed AnnotationPrefix with InvalidPrefix, and returns
    /// NoSuchBucket/NoSuchKey for missing resources rather than an empty listing.
    /// </summary>
    [Test]
    [DisplayName("s3.list-object-annotations-invalid-prefix")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;

        // 1. Nonexistent bucket -> NoSuchBucket.
        var missingBucket = FidelityContext.UniqueName("sim2real-nobucket-");
        var errBucket = await S3Fidelity.Error(() => S3Fidelity.ListAnnotations(s3, missingBucket, "whatever"));
        Ensure(errBucket.Code == "NoSuchBucket", $"expected NoSuchBucket, got {errBucket}");
        Ensure(errBucket.Message == "The specified bucket does not exist", errBucket.ToString());

        var bucket = await S3Fidelity.CreateBucket(ctx, "sim2real-behav-");

        // 2. Existing bucket, missing key -> NoSuchKey.
        var errKey = await S3Fidelity.Error(() => S3Fidelity.ListAnnotations(s3, bucket, "does-not-exist.bin"));
        Ensure(errKey.Code == "NoSuchKey", $"expected NoSuchKey, got {errKey}");
        Ensure(errKey.Message == "The specified key does not exist.", errKey.ToString());

        // 3. Existing object, malformed prefix -> InvalidPrefix. AnnotationPrefix is a
        //    request-level query filter (not an annotation name being created), so real
        //    S3 does not apply the annotation-name validation rules (reserved "aws-"
        //    prefix, character set, etc.) to it -- a prefix like "aws-reserved-prefix"
        //    is a perfectly legal (if non-matching) filter value. What real S3 does
        //    reject is a filter value that cannot possibly match any legal annotation
        //    name, such as one containing a NUL byte -- and it does so with the
        //    dedicated "InvalidPrefix" error code, not the generic "InvalidArgument".
        const string key = "annotated.bin";
        await s3.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = key, ContentBody = "payload" });
        ctx.OnCleanup(() => s3.DeleteObjectAsync(bucket, key));
        await S3Fidelity.PutAnnotation(s3, bucket, key, "real-annotation", "1"u8.ToArray());
        var errPrefix = await S3Fidelity.Error(() => S3Fidelity.ListAnnotations(s3, bucket, key, "bad\0prefix"));
        Ensure(errPrefix.Code == "InvalidPrefix", $"expected InvalidPrefix, got {errPrefix}");
        Ensure(errPrefix.Message == "The annotation prefix you have provided is invalid.", errPrefix.ToString());

        // A valid prefix must still work and find the real annotation.
        var listing = await S3Fidelity.ListAnnotations(s3, bucket, key, "real-");
        var names = S3Fidelity.AnnotationNames(listing);
        Ensure(names.Contains("real-annotation"), $"expected 'real-annotation' in {Py.Repr(names)}");
    }
}
