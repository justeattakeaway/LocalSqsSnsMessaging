// Translated from behavioral/s3/check_deep_s3_storage_class_deep_archive_get_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.deep-s3-storage-class-deep-archive-get-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class DeepS3StorageClassDeepArchiveGetRejected
{
    /// <summary>
    /// An object that HeadObject and ListObjectsV2 report as DEEP_ARCHIVE with its exact size is refused
    /// by GetObject, whole and ranged, with InvalidObjectState and its exact message, while a STANDARD
    /// sibling serves its bytes before and after.
    /// </summary>
    [Test]
    [DisplayName("s3.deep-s3-storage-class-deep-archive-get-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var bucket = await S3Fidelity.CreateBucket(ctx, "sim2real-dsc-da-");
        const string key = "archive/deep.bin";
        var body = "deep-archive-payload-0123456789"u8.ToArray();
        const string controlKey = "warm/standard.bin";
        var controlBody = "standard-control-payload"u8.ToArray();

        // Positive control: GetObject works in this bucket for an active-tier object.
        await S3Fidelity.PutWithClass(s3, bucket, controlKey, controlBody);
        var got = await S3Fidelity.GetBytes(s3, bucket, controlKey);
        Ensure(got.SequenceEqual(controlBody), $"STANDARD control returned {S3Fidelity.Head(got)}, expected {S3Fidelity.Head(controlBody)}");

        await S3Fidelity.PutWithClass(s3, bucket, key, body, "DEEP_ARCHIVE");
        var (head, restore) = await S3Fidelity.HeadWithRestore(s3, bucket, key);
        Ensure(head.StorageClass?.Value == "DEEP_ARCHIVE", $"HeadObject StorageClass was {Py.Repr(head.StorageClass?.Value)}, expected 'DEEP_ARCHIVE'");
        Ensure(head.ContentLength == body.Length, $"HeadObject ContentLength was {head.ContentLength}, expected {body.Length}");
        Ensure(restore is null, $"a never-restored object carried Restore={Py.Repr(restore)}");
        var listed = (await s3.ListObjectsV2Async(new ListObjectsV2Request { BucketName = bucket, Prefix = "archive/" })).S3Objects ?? [];
        Ensure(listed.Select(o => o.Key).SequenceEqual([key]), $"ListObjectsV2 under archive/ returned {Py.Repr(listed.Select(o => o.Key).ToList())}");
        Ensure(listed[0].StorageClass?.Value == "DEEP_ARCHIVE", $"ListObjectsV2 StorageClass was {Py.Repr(listed[0].StorageClass?.Value)}, expected 'DEEP_ARCHIVE'");
        Ensure(listed[0].Size == body.Length, $"ListObjectsV2 Size was {listed[0].Size}");

        (string? Code, string? Message) archived = ("InvalidObjectState", "The operation is not valid for the object's storage class");
        var code = await S3Fidelity.Refusal(() => s3.GetObjectAsync(bucket, key));
        Ensure(code == archived, $"GetObject on DEEP_ARCHIVE returned {S3Fidelity.Repr(code)}, expected InvalidObjectState with AWS's exact message");
        var ranged = await S3Fidelity.Refusal(() => s3.GetObjectAsync(new GetObjectRequest { BucketName = bucket, Key = key, ByteRange = new ByteRange(0, 3) }));
        Ensure(ranged == archived, $"ranged GetObject on DEEP_ARCHIVE returned {S3Fidelity.Repr(ranged)}, expected InvalidObjectState");

        // The refusal is per-object, not a bucket-wide break.
        var again = await S3Fidelity.GetBytes(s3, bucket, controlKey);
        Ensure(again.SequenceEqual(controlBody), $"STANDARD control after refusal returned {S3Fidelity.Head(again)}");
    }
}
