// Translated from behavioral/s3/check_deep_s3_storage_class_restore_zero_days_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.deep-s3-storage-class-restore-zero-days-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class DeepS3StorageClassRestoreZeroDaysRejected
{
    /// <summary>
    /// RestoreObject with Days=0 on a GLACIER object is refused with InvalidArgument and its exact
    /// message and starts no restore (no Restore header, GetObject still InvalidObjectState); Days=1 is then
    /// accepted with 202 and an ongoing Restore header.
    /// </summary>
    [Test]
    [DisplayName("s3.deep-s3-storage-class-restore-zero-days-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        const string zeroDaysMessage = "restoration days should be at least 1";
        var s3 = ctx.S3;
        var bucket = await S3Fidelity.CreateBucket(ctx, "sim2real-dsc-rz-");
        const string key = "archive/glacier.bin";
        await S3Fidelity.PutWithClass(s3, bucket, key, "glacier-zero-days"u8.ToArray(), "GLACIER");
        Ensure((await s3.GetObjectMetadataAsync(bucket, key)).StorageClass?.Value == "GLACIER", "assert StorageClass == \"GLACIER\"");
        var refusal = await S3Fidelity.Refusal(() => S3Fidelity.Restore(s3, bucket, key, 0, "Bulk"));
        Ensure(refusal == ("InvalidArgument", zeroDaysMessage), $"RestoreObject Days=0 returned {S3Fidelity.Repr(refusal)}, expected InvalidArgument");
        var restore = await S3Fidelity.RestoreHeader(s3, bucket, key);
        Ensure(restore is null, $"a refused Days=0 restore left Restore={Py.Repr(restore)}");
        var getRefusal = await S3Fidelity.Refusal(() => s3.GetObjectAsync(bucket, key));
        Ensure(
            getRefusal == ("InvalidObjectState", "The operation is not valid for the object's storage class"),
            $"GetObject after refused restore returned {S3Fidelity.Repr(getRefusal)}");

        // Positive control: identical request, Days=1.
        var status = (int)(await S3Fidelity.Restore(s3, bucket, key, 1, "Bulk")).HttpStatusCode;
        Ensure(status == 202, $"Days=1 restore returned HTTP {status}, expected 202");
        var header = await S3Fidelity.PollRestoreHeader(s3, bucket, key);
        Ensure(header == "ongoing-request=\"true\"", $"Restore header after Days=1 was {Py.Repr(header)}");
    }
}
