// Translated from behavioral/s3/check_restore_object_ttl_expiry_reverts.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.restore-object-ttl-expiry-reverts</summary>
[Category("AwsEvaluationSuite")]
public sealed class RestoreObjectTtlExpiryReverts
{
    /// <summary>
    /// RestoreObject with a finite Days value answers 202 and HeadObject reports an ongoing Restore
    /// header; a second RestoreObject while it is open fails with RestoreAlreadyInProgress and its exact
    /// message and leaves the Restore header and storage class unchanged.
    /// </summary>
    [Test]
    [DisplayName("s3.restore-object-ttl-expiry-reverts")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var bucket = await S3Fidelity.CreateBucket(ctx, "sim2real-behav-ttl");
        const string key = "archived.bin";
        await S3Fidelity.PutWithClass(s3, bucket, key, "ttl-window-payload"u8.ToArray(), "GLACIER");

        // No restore window is open yet -> nothing to be "already in progress" against.
        var first = await S3Fidelity.Restore(s3, bucket, key, 1);
        Ensure((int)first.HttpStatusCode == 202, $"first RestoreObject call should be accepted with 202, got {(int)first.HttpStatusCode}");
        var (head, restoreHeader) = await S3Fidelity.HeadWithRestore(s3, bucket, key);
        Ensure(restoreHeader is not null, "HeadObject should show an open restore TTL window");
        Ensure(restoreHeader!.Contains("ongoing-request=\"true\"", StringComparison.Ordinal), $"unexpected Restore header: {restoreHeader}");

        // A second RestoreObject while the window is already open is rejected -- this
        // is the deterministic, fast-observable half of the TTL-window state machine.
        var (code, message) = await S3Fidelity.Refusal(() => S3Fidelity.Restore(s3, bucket, key, 1));
        Ensure(code == "RestoreAlreadyInProgress", $"expected RestoreAlreadyInProgress for a concurrent restore, got {Py.Repr(code)}");
        Ensure(message == "Object restore is already in progress", $"expected the exact RestoreAlreadyInProgress message, got {Py.Repr(message)}");

        // Effect absent: the rejected second call did not open a second window or alter
        // the one already in flight.
        var (headAfter, restoreAfter) = await S3Fidelity.HeadWithRestore(s3, bucket, key);
        Ensure(restoreAfter == restoreHeader, $"the rejected concurrent restore changed the window: {Py.Repr(restoreHeader)} -> {Py.Repr(restoreAfter)}");
        Ensure(headAfter.StorageClass?.Value == head.StorageClass?.Value, "the rejected concurrent restore changed the object's storage class");
    }
}
