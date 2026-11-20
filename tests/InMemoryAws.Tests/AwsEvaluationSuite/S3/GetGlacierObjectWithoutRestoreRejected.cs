// Translated from behavioral/s3/check_get_glacier_object_without_restore_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.get-glacier-object-without-restore-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class GetGlacierObjectWithoutRestoreRejected
{
    /// <summary>
    /// GetObject on an unrestored GLACIER object, which HeadObject and ListObjectsV2 still show, is
    /// refused with InvalidObjectState and its exact message, while a STANDARD sibling returns its bytes.
    /// </summary>
    [Test]
    [DisplayName("s3.get-glacier-object-without-restore-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var bucket = await S3Fidelity.CreateBucket(ctx, "sim2real-behav-rstneg");
        const string key = "cold.bin";
        const string controlKey = "warm.bin";
        var controlBody = "warm-storage-payload"u8.ToArray();

        // Positive control: an ordinary object in the same bucket is readable end to end,
        // so the InvalidObjectState assertion below cannot be scoring a target that just
        // denies every GetObject.
        await S3Fidelity.PutWithClass(s3, bucket, controlKey, controlBody);
        Ensure((await S3Fidelity.GetBytes(s3, bucket, controlKey)).SequenceEqual(controlBody), "positive control GetObject must return its own bytes");
        await S3Fidelity.PutWithClass(s3, bucket, key, "cold-storage-payload"u8.ToArray(), "GLACIER");

        // Control plane is unaffected: the object is fully visible.
        var (head, restore) = await S3Fidelity.HeadWithRestore(s3, bucket, key);
        Ensure(head.StorageClass?.Value == "GLACIER", "assert head[\"StorageClass\"] == \"GLACIER\"");
        Ensure(restore is null, $"unrestored object must carry no Restore header, got {Py.Repr(restore)}");
        var listing = await s3.ListObjectsV2Async(new ListObjectsV2Request { BucketName = bucket });
        Ensure((listing.S3Objects ?? []).Any(o => o.Key == key), "assert any(o[\"Key\"] == key for o in listing.get(\"Contents\", []))");

        // Data plane GET is rejected until a restore is requested and completes.
        var err = await S3Fidelity.Refusal(() => s3.GetObjectAsync(bucket, key));
        Ensure(err.Code == "InvalidObjectState", $"expected InvalidObjectState, got {S3Fidelity.Repr(err)}");
        Ensure(err.Message == "The operation is not valid for the object's storage class", err.Message ?? "None");
    }
}
