// Translated from behavioral/s3/check_deep_s3_storage_class_deep_archive_expedited_restore_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.deep-s3-storage-class-deep-archive-expedited-restore-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class DeepS3StorageClassDeepArchiveExpeditedRestoreRejected
{
    /// <summary>
    /// RestoreObject with Tier=Expedited on a DEEP_ARCHIVE object is refused with InvalidTier and its
    /// exact message and starts no restore; a Bulk restore then answers 202 with an ongoing Restore header,
    /// and a repeat is RestoreAlreadyInProgress.
    /// </summary>
    [Test]
    [DisplayName("s3.deep-s3-storage-class-deep-archive-expedited-restore-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var bucket = await S3Fidelity.CreateBucket(ctx, "sim2real-dsc-dx-");
        const string key = "archive/deep.bin";
        await S3Fidelity.PutWithClass(s3, bucket, key, "deep-archive-restore-tier"u8.ToArray(), "DEEP_ARCHIVE");
        var (head, restore) = await S3Fidelity.HeadWithRestore(s3, bucket, key);
        Ensure(head.StorageClass?.Value == "DEEP_ARCHIVE", $"StorageClass was {Py.Repr(head.StorageClass?.Value)}");
        Ensure(restore is null, $"fresh object carried Restore={Py.Repr(restore)}");

        // Break exactly one thing: the Expedited tier.
        var code = await S3Fidelity.Refusal(() => S3Fidelity.Restore(s3, bucket, key, 1, "Expedited"));
        Ensure(
            code == ("InvalidTier", "Retrieval option is not supported by this storage class."),
            $"Expedited restore of DEEP_ARCHIVE returned {S3Fidelity.Repr(code)}, expected InvalidTier with AWS's exact message");
        var (after, afterRestore) = await S3Fidelity.HeadWithRestore(s3, bucket, key);
        Ensure(afterRestore is null, $"a refused Expedited restore still left Restore={Py.Repr(afterRestore)} on the object");
        Ensure(after.StorageClass?.Value == "DEEP_ARCHIVE", $"StorageClass changed to {Py.Repr(after.StorageClass?.Value)}");

        // Positive control: the same object, same Days, Bulk tier -> an accepted, visible restore.
        var resp = await S3Fidelity.Restore(s3, bucket, key, 1, "Bulk");
        var status = (int)resp.HttpStatusCode;
        Ensure(status == 202, $"first Bulk restore returned HTTP {status}, expected 202 Accepted");
        var header = await S3Fidelity.PollRestoreHeader(s3, bucket, key);
        Ensure(
            header == "ongoing-request=\"true\"",
            $"after an accepted Bulk restore HeadObject Restore was {Py.Repr(header)}, expected ongoing-request=\"true\"");

        // A second identical request while the first is in flight is refused.
        var dup = await S3Fidelity.Refusal(() => S3Fidelity.Restore(s3, bucket, key, 1, "Bulk"));
        Ensure(dup == ("RestoreAlreadyInProgress", "Object restore is already in progress"), $"duplicate Bulk restore returned {S3Fidelity.Repr(dup)}");
    }
}
