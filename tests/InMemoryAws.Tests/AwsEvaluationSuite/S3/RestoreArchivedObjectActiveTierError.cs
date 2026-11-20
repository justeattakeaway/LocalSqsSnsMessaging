// Translated from behavioral/s3/check_restore_archived_object_active_tier_error.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.restore-archived-object-active-tier-error</summary>
[Category("AwsEvaluationSuite")]
public sealed class RestoreArchivedObjectActiveTierError
{
    /// <summary>
    /// RestoreObject on a never-archived STANDARD object returns InvalidObjectState and starts no
    /// restore; the same call on an archived GLACIER sibling succeeds, so the rejection is specific
    /// to storage class.
    /// </summary>
    [Test]
    [DisplayName("s3.restore-archived-object-active-tier-error")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var bucket = await S3Fidelity.CreateBucket(ctx, "sim2real-behav-");
        const string key = "active-object.txt";
        await S3Fidelity.PutWithClass(s3, bucket, key, "hello-active-tier"u8.ToArray());
        ctx.OnCleanup(() => s3.DeleteObjectAsync(bucket, key));

        // Sanity: the object really is in the Standard (active) storage tier.
        var head = await s3.GetObjectMetadataAsync(bucket, key);
        Ensure((head.StorageClass?.Value ?? "STANDARD") == "STANDARD", $"expected STANDARD storage class for a freshly-PUT object, got {Py.Repr(head.StorageClass?.Value)}");
        var refusal = await S3Fidelity.Refusal(() => S3Fidelity.Restore(s3, bucket, key, 1));
        Ensure(
            refusal == ("InvalidObjectState", "Restore is not allowed for the object's current storage class"),
            $"expected InvalidObjectState restoring an already-active object, got {S3Fidelity.Repr(refusal)}");

        // Effect-absent: the rejected call left no restore job in progress.
        var restoreAfter = await S3Fidelity.RestoreHeader(s3, bucket, key);
        Ensure(restoreAfter is null, $"no restore should be in progress after a rejected RestoreObject, got {Py.Repr(restoreAfter)}");

        // Positive control: the identical shape of call succeeds against an object that
        // IS archived, closing the gap where a target that unconditionally rejects every
        // RestoreObject call would otherwise pass this check.
        const string archivedKey = "archived-control.txt";
        await S3Fidelity.PutWithClass(s3, bucket, archivedKey, "cold-control"u8.ToArray(), "GLACIER");
        ctx.OnCleanup(() => s3.DeleteObjectAsync(bucket, archivedKey));
        if (await FidelityContext.CaptureError(() => S3Fidelity.Restore(s3, bucket, archivedKey, 1, "Standard")) is { } error)
        {
            throw Fail($"RestoreObject on an archived (GLACIER) object should succeed, got {error}");
        }
    }
}
