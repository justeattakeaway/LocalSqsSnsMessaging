// Translated from behavioral/s3/check_copy_object_glacier_not_in_active_tier_error.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.copy-object-glacier-not-in-active-tier-error</summary>
[Category("AwsEvaluationSuite")]
public sealed class CopyObjectGlacierNotInActiveTierError
{
    /// <summary>
    /// CopyObject sourced from a GLACIER-class object that has not been restored to the
    /// active tier returns InvalidObjectState and the destination object is never
    /// created; the same CopyObject call against a STANDARD source succeeds byte-for-byte.
    /// </summary>
    [Test]
    [DisplayName("s3.copy-object-glacier-not-in-active-tier-error")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var bucket = await S3Fidelity.CreateBucket(ctx, "sim2real-behav-");
        const string standardKey = "source-standard.bin";
        var standardBody = Repeat("hot-data-copies-fine", 10);
        await S3Fidelity.PutWithClass(s3, bucket, standardKey, standardBody);
        const string glacierKey = "source-glacier.bin";
        await S3Fidelity.PutWithClass(s3, bucket, glacierKey, Repeat("cold-archived-data", 10), "GLACIER");
        var head = await s3.GetObjectMetadataAsync(bucket, glacierKey);
        Ensure(head.StorageClass?.Value == "GLACIER", $"expected GLACIER storage class, got {Py.Repr(head.StorageClass?.Value)}");

        // Positive control: copying a STANDARD-tier source succeeds and is byte-identical.
        const string standardDestKey = "dest-from-standard.bin";
        await Copy(s3, bucket, standardKey, standardDestKey);
        var copiedBody = await S3Fidelity.GetBytes(s3, bucket, standardDestKey);
        Ensure(copiedBody.SequenceEqual(standardBody), "copied STANDARD-tier object must be byte-identical to its source");

        // Negative: copying from the un-restored GLACIER source is rejected.
        const string glacierDestKey = "dest-from-glacier.bin";
        var refusal = await S3Fidelity.Refusal(() => Copy(s3, bucket, glacierKey, glacierDestKey));

        // Real S3 rejects CopyObject from an un-restored GLACIER source with InvalidObjectState
        // (the same code GetObject/HeadObject use for archived objects), not the
        // ObjectNotInActiveTierError name suggested by some documentation.
        Ensure(refusal == CopyArchived, $"expected {S3Fidelity.Repr(CopyArchived)}, got {S3Fidelity.Repr(refusal)}");

        // The rejected copy must not have created a destination object.
        var notFound = await S3Fidelity.Refusal(() => s3.GetObjectMetadataAsync(bucket, glacierDestKey));
        Ensure(notFound == HeadAbsent, $"expected the copy destination absent {S3Fidelity.Repr(HeadAbsent)}, got {S3Fidelity.Repr(notFound)}");
    }

    private static byte[] Repeat(string text, int times) => System.Text.Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat(text, times)));

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
