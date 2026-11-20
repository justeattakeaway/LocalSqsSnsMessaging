// Translated from behavioral/s3/check_deep_s3_storage_class_copy_in_place_to_deep_archive_closes_reads.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.deep-s3-storage-class-copy-in-place-to-deep-archive-closes-reads</summary>
[Category("AwsEvaluationSuite")]
public sealed class DeepS3StorageClassCopyInPlaceToDeepArchiveClosesReads
{
    /// <summary>
    /// An in-place CopyObject to DEEP_ARCHIVE keeps size and ETag but closes reads: GetObject and
    /// a copy back to STANDARD are refused with InvalidObjectState, and the class stays.
    /// </summary>
    [Test]
    [DisplayName("s3.deep-s3-storage-class-copy-in-place-to-deep-archive-closes-reads")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        (string? Code, string? Message) getArchived = ("InvalidObjectState", "The operation is not valid for the object's storage class");
        var s3 = ctx.S3;
        var bucket = await S3Fidelity.CreateBucket(ctx, "sim2real-dsc-cp-");
        const string key = "data/object.bin";
        var body = "readable-until-archived-0123456789"u8.ToArray();
        var etag = (await S3Fidelity.PutWithClass(s3, bucket, key, body)).ETag;
        var before = await S3Fidelity.GetBytes(s3, bucket, key);
        Ensure(before.SequenceEqual(body), $"STANDARD GetObject returned {S3Fidelity.Head(before)}, expected {S3Fidelity.Head(body)}");
        await Copy(s3, bucket, key, key, "DEEP_ARCHIVE");
        var head = await s3.GetObjectMetadataAsync(bucket, key);
        Ensure(head.StorageClass?.Value == "DEEP_ARCHIVE", $"after re-class StorageClass was {Py.Repr(head.StorageClass?.Value)}");
        Ensure(head.ContentLength == body.Length, $"after re-class ContentLength was {head.ContentLength}");
        Ensure(head.ETag == etag, $"after re-class ETag {Py.Repr(head.ETag)} != original {Py.Repr(etag)}");
        var listed = ((await s3.ListObjectsV2Async(new ListObjectsV2Request { BucketName = bucket, Prefix = "data/" })).S3Objects ?? [])
            .ToDictionary(o => o.Key, o => o.StorageClass?.Value);
        Ensure(listed.Count == 1 && listed.GetValueOrDefault(key) == "DEEP_ARCHIVE", $"ListObjectsV2 after re-class: {Py.Repr(listed.Select(l => $"{l.Key}={l.Value}").ToList())}");
        var got = await S3Fidelity.Refusal(() => s3.GetObjectAsync(bucket, key));
        Ensure(got == getArchived, $"GetObject after re-class returned {S3Fidelity.Repr(got)}, expected {S3Fidelity.Repr(getArchived)}");
        var back = await S3Fidelity.Refusal(() => Copy(s3, bucket, key, key, "STANDARD"));
        Ensure(back == CopyArchived, $"copy back to STANDARD returned {S3Fidelity.Repr(back)}, expected {S3Fidelity.Repr(CopyArchived)}");
        var still = await s3.GetObjectMetadataAsync(bucket, key);
        Ensure(still.StorageClass?.Value == "DEEP_ARCHIVE", $"refused copy-back changed class to {Py.Repr(still.StorageClass?.Value)}");
    }

    private static readonly (string? Code, string? Message) CopyArchived = ("InvalidObjectState", "Operation is not valid for the source object's storage class");

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
