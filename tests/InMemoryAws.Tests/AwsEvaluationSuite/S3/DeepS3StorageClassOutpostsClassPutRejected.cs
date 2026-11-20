// Translated from behavioral/s3/check_deep_s3_storage_class_outposts_class_put_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.deep-s3-storage-class-outposts-class-put-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class DeepS3StorageClassOutpostsClassPutRejected
{
    /// <summary>
    /// PutObject with StorageClass=OUTPOSTS into a regional general-purpose bucket is refused with
    /// InvalidStorageClass and writes nothing (HeadObject 404, empty listing); a ONEZONE_IA write to the
    /// same key then succeeds.
    /// </summary>
    [Test]
    [DisplayName("s3.deep-s3-storage-class-outposts-class-put-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var bucket = await S3Fidelity.CreateBucket(ctx, "sim2real-dsc-op-");
        const string key = "target/object.bin";
        var body = "outposts-class-refused"u8.ToArray();
        var error = await S3Fidelity.Refusal(() => S3Fidelity.PutWithClass(s3, bucket, key, body, "OUTPOSTS"));
        Ensure(
            error == ("InvalidStorageClass", "The storage class you specified is not valid"),
            $"PutObject StorageClass=OUTPOSTS returned {S3Fidelity.Repr(error)}, expected InvalidStorageClass");
        var missing = await S3Fidelity.Refusal(() => s3.GetObjectMetadataAsync(bucket, key));
        Ensure(missing == ("404", "Not Found"), $"HeadObject after the refused PUT returned {S3Fidelity.Repr(missing)}, expected 404");
        var listed = (await s3.ListObjectsV2Async(new ListObjectsV2Request { BucketName = bucket, Prefix = "target/" })).S3Objects ?? [];
        Ensure(listed.Count == 0, $"refused PUT still left objects under target/: {Py.Repr(listed.Select(o => o.Key).ToList())}");

        // Positive control: same key, same body, a valid deep_s3_storage_class_outposts_class_put_rejected0 class.
        await S3Fidelity.PutWithClass(s3, bucket, key, body, "ONEZONE_IA");
        var head = await s3.GetObjectMetadataAsync(bucket, key);
        Ensure(head.StorageClass?.Value == "ONEZONE_IA", $"control StorageClass was {Py.Repr(head.StorageClass?.Value)}");
        var data = await S3Fidelity.GetBytes(s3, bucket, key);
        Ensure(data.SequenceEqual(body), $"control GetObject returned {S3Fidelity.Head(data)}");
    }
}
