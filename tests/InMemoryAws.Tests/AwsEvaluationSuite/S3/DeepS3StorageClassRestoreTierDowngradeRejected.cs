// Translated from behavioral/s3/check_deep_s3_storage_class_restore_tier_downgrade_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.deep-s3-storage-class-restore-tier-downgrade-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class DeepS3StorageClassRestoreTierDowngradeRejected
{
    /// <summary>
    /// During a Bulk restore of a GLACIER object, a Standard-tier RestoreObject is accepted (202) as an
    /// upgrade, while repeating Bulk and a Bulk after the upgrade are refused with RestoreAlreadyInProgress
    /// and its exact message; the restore stays ongoing.
    /// </summary>
    [Test]
    [DisplayName("s3.deep-s3-storage-class-restore-tier-downgrade-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        const string inProgress = "Object restore is already in progress";
        var s3 = ctx.S3;
        var bucket = await S3Fidelity.CreateBucket(ctx, "sim2real-dsc-tu-");
        const string key = "archive/glacier.bin";
        await S3Fidelity.PutWithClass(s3, bucket, key, "glacier-tier-upgrade"u8.ToArray(), "GLACIER");
        Ensure((await s3.GetObjectMetadataAsync(bucket, key)).StorageClass?.Value == "GLACIER", "assert StorageClass == \"GLACIER\"");

        Task<RestoreObjectResponse> Restore(string tier) => S3Fidelity.Restore(s3, bucket, key, 1, tier);

        var first = (int)(await Restore("Bulk")).HttpStatusCode;
        Ensure(first == 202, $"initial Bulk restore returned HTTP {first}, expected 202");
        var header = await S3Fidelity.PollRestoreHeader(s3, bucket, key);
        Ensure(header == "ongoing-request=\"true\"", $"Restore header after Bulk was {Py.Repr(header)}");
        var (same, sameMessage) = await ErrorCode(() => Restore("Bulk"));
        Ensure(same == "RestoreAlreadyInProgress", $"repeating the in-flight Bulk tier returned {Py.Repr(same)}");
        Ensure(sameMessage == inProgress, $"repeating the in-flight Bulk tier said {Py.Repr(sameMessage)}");

        // Positive control: the upgrade to a faster tier is accepted.
        var upgraded = (int)(await Restore("Standard")).HttpStatusCode;
        Ensure(upgraded == 202, $"Bulk->Standard upgrade returned HTTP {upgraded}, expected 202");

        // Break exactly one thing: go back down to the slower tier.
        var (down, downMessage) = await ErrorCode(() => Restore("Bulk"));
        Ensure(down == "RestoreAlreadyInProgress", $"Standard->Bulk downgrade returned {Py.Repr(down)}");
        Ensure(downMessage == inProgress, $"Standard->Bulk downgrade said {Py.Repr(downMessage)}");
        var (still, stillRestore) = await S3Fidelity.HeadWithRestore(s3, bucket, key);
        Ensure(stillRestore == "ongoing-request=\"true\"", $"after the refused downgrade the Restore header was {Py.Repr(stillRestore)}");
        Ensure(still.StorageClass?.Value == "GLACIER", $"StorageClass changed to {Py.Repr(still.StorageClass?.Value)}");
    }

    /// <summary>_error_code(fn): (code, message) of the ClientError fn raises; an unexpected success raises.</summary>
    private static Task<(string? Code, string? Message)> ErrorCode(Func<Task> fn) =>
        S3Fidelity.ClientError(fn, "the call succeeded where an error was expected");
}
