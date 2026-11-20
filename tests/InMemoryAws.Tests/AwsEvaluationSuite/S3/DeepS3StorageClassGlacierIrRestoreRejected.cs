// Translated from behavioral/s3/check_deep_s3_storage_class_glacier_ir_restore_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.deep-s3-storage-class-glacier-ir-restore-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class DeepS3StorageClassGlacierIrRestoreRejected
{
    /// <summary>
    /// A GLACIER_IR object reads back byte for byte without a restore, and RestoreObject on it is
    /// refused with InvalidObjectState and its exact message, while a GLACIER sibling refuses GetObject
    /// instead.
    /// </summary>
    [Test]
    [DisplayName("s3.deep-s3-storage-class-glacier-ir-restore-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var bucket = await S3Fidelity.CreateBucket(ctx, "sim2real-dsc-ir-");
        const string irKey = "ir/instant.bin";
        var irBody = "glacier-instant-retrieval-bytes"u8.ToArray();
        const string glKey = "gl/flexible.bin";
        await S3Fidelity.PutWithClass(s3, bucket, irKey, irBody, "GLACIER_IR");
        await S3Fidelity.PutWithClass(s3, bucket, glKey, "glacier-flexible-bytes"u8.ToArray(), "GLACIER");
        var head = await s3.GetObjectMetadataAsync(bucket, irKey);
        Ensure(head.StorageClass?.Value == "GLACIER_IR", $"HeadObject StorageClass was {Py.Repr(head.StorageClass?.Value)}");

        // Positive: GLACIER_IR serves its bytes with no restore.
        using (var got = await s3.GetObjectAsync(bucket, irKey))
        {
            var data = await S3Fidelity.ReadBody(got);
            Ensure(data.SequenceEqual(irBody), $"GLACIER_IR GetObject returned {S3Fidelity.Head(data)}, expected {S3Fidelity.Head(irBody)}");
            Ensure(got.StorageClass?.Value == "GLACIER_IR", $"GetObject StorageClass was {Py.Repr(got.StorageClass?.Value)}");
        }

        // Break one thing: ask for a restore of an object that is already retrievable.
        var err = await S3Fidelity.Refusal(() => S3Fidelity.Restore(s3, bucket, irKey, 1));
        Ensure(err.Code == "InvalidObjectState", $"RestoreObject on GLACIER_IR returned {S3Fidelity.Repr(err)}, expected InvalidObjectState");
        Ensure(err.Message == "Restore is not allowed for the object's current storage class", S3Fidelity.Repr(err));
        var (after, restore) = await S3Fidelity.HeadWithRestore(s3, bucket, irKey);
        Ensure(restore is null, $"a refused restore left Restore={Py.Repr(restore)}");
        Ensure(after.StorageClass?.Value == "GLACIER_IR", $"StorageClass changed to {Py.Repr(after.StorageClass?.Value)}");

        // Contrast: the GLACIER sibling is on the other side of the boundary.
        var glErr = await S3Fidelity.Refusal(() => s3.GetObjectAsync(bucket, glKey));
        Ensure(glErr.Code == "InvalidObjectState", $"GetObject on GLACIER sibling returned {S3Fidelity.Repr(glErr)}");
        Ensure(glErr.Message == "The operation is not valid for the object's storage class", S3Fidelity.Repr(glErr));
    }
}
