// Translated from behavioral/s3/check_restore_object_on_standard_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.restore-object-on-standard-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class RestoreObjectOnStandardRejected
{
    /// <summary>
    /// RestoreObject on a STANDARD object fails with InvalidObjectState and its exact message and starts
    /// no restore, while the same call on a GLACIER object in the same bucket starts an ongoing restore.
    /// </summary>
    [Test]
    [DisplayName("s3.restore-object-on-standard-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var name = await S3Fidelity.CreateBucket(ctx, "sim2real-behav-bkt-");

        // Positive control: RestoreObject against an archived object is accepted.
        const string archivedKey = "archived.bin";
        await S3Fidelity.PutWithClass(s3, name, archivedKey, "cold-data"u8.ToArray(), "GLACIER");
        await S3Fidelity.Restore(s3, name, archivedKey, 1, "Standard");
        var restoreHdr = await S3Fidelity.RestoreHeader(s3, name, archivedKey) ?? string.Empty;
        Ensure(
            restoreHdr.Contains("ongoing-request=\"true\"", StringComparison.Ordinal),
            $"expected an ongoing-restore marker after RestoreObject on an archived object, got {Py.Repr(restoreHdr)}");

        // Negative: the identical call against a STANDARD (never-archived) object is rejected.
        const string key = "not-archived.txt";
        await S3Fidelity.PutWithClass(s3, name, key, "x"u8.ToArray());
        var refusal = await S3Fidelity.Refusal(() => S3Fidelity.Restore(s3, name, key, 1, "Standard"));
        Ensure(
            refusal == ("InvalidObjectState", "Restore is not allowed for the object's current storage class"),
            $"expected InvalidObjectState restoring a STANDARD object, got {S3Fidelity.Repr(refusal)}");

        // the object is untouched and has no Restore header
        var restore = await S3Fidelity.RestoreHeader(s3, name, key);
        Ensure(restore is null, $"no restore should be in progress, got {Py.Repr(restore)}");
    }
}
