// Translated from behavioral/s3/check_copy_object_glacier_source_error.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.copy-object-glacier-source-error</summary>
[Category("AwsEvaluationSuite")]
public sealed class CopyObjectGlacierSourceError
{
    /// <summary>
    /// CopyObject from an unrestored GLACIER source is refused with InvalidObjectState and its exact
    /// message and creates no destination, while the same copy from a STANDARD source is byte-identical.
    /// </summary>
    [Test]
    [DisplayName("s3.copy-object-glacier-source-error")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var bucket = await S3Fidelity.CreateBucket(ctx, "sim2real-behav-");

        // Positive control: CopyObject from a STANDARD-tier source works end to end.
        const string standardKey = "standard-src.bin";
        var standardBody = "hot-data-copies-fine"u8.ToArray();
        const string standardDst = "copy-of-standard-src.bin";
        await S3Fidelity.PutWithClass(s3, bucket, standardKey, standardBody);
        await Copy(s3, bucket, standardKey, standardDst);
        var copiedBody = await S3Fidelity.GetBytes(s3, bucket, standardDst);
        Ensure(
            copiedBody.SequenceEqual(standardBody),
            $"positive control failed: expected {S3Fidelity.Head(standardBody)} from the STANDARD-tier copy, got {S3Fidelity.Head(copiedBody)}");

        const string srcKey = "glacier-src.bin";
        const string dstKey = "copy-of-glacier-src.bin";
        await S3Fidelity.PutWithClass(s3, bucket, srcKey, "cold-data"u8.ToArray(), "GLACIER");
        ctx.OnCleanup(() => s3.DeleteObjectAsync(bucket, srcKey));
        var (head, restore) = await S3Fidelity.HeadWithRestore(s3, bucket, srcKey);
        Ensure(head.StorageClass?.Value == "GLACIER", $"expected the source to land in GLACIER, got {Py.Repr(head.StorageClass?.Value)}");

        // Never restored: no x-amz-restore header should be present yet.
        Ensure(restore is null, $"expected no Restore header before any restore request, got {Py.Repr(restore)}");
        var refusal = await S3Fidelity.Refusal(() => Copy(s3, bucket, srcKey, dstKey));
        Ensure(refusal == CopyArchived, $"expected {S3Fidelity.Repr(CopyArchived)} copying an un-restored GLACIER source, got {S3Fidelity.Repr(refusal)}");

        // Independent read at the destination: the rejected copy must not have created it.
        var dst = await S3Fidelity.Refusal(() => s3.GetObjectMetadataAsync(bucket, dstKey));
        Ensure(dst == HeadAbsent, $"expected the copy destination absent {S3Fidelity.Repr(HeadAbsent)} after the rejected copy, got {S3Fidelity.Repr(dst)}");
    }

    private static readonly (string? Code, string? Message) CopyArchived = ("InvalidObjectState", "Operation is not valid for the source object's storage class");
    private static readonly (string? Code, string? Message) HeadAbsent = ("404", "Not Found");

    private static Task<CopyObjectResponse> Copy(AmazonS3Client s3, string bucket, string sourceKey, string destinationKey, string? storageClass = null) =>
        s3.CopyObjectAsync(new CopyObjectRequest
        {
            SourceBucket = bucket,
            SourceKey = sourceKey,
            DestinationBucket = bucket,
            DestinationKey = destinationKey,
            StorageClass = storageClass is null ? null : new S3StorageClass(storageClass),
        });
}
