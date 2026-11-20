// Translated from behavioral/s3/check_restore_object_duplicate_in_progress_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.restore-object-duplicate-in-progress-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class RestoreObjectDuplicateInProgressRejected
{
    /// <summary>
    /// A second RestoreObject while the first Standard-tier restore of a GLACIER object is ongoing fails
    /// with RestoreAlreadyInProgress and its exact message, and the original restore stays ongoing.
    /// </summary>
    [Test]
    [DisplayName("s3.restore-object-duplicate-in-progress-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var bucket = await S3Fidelity.CreateBucket(ctx, "sim2real-behav-bkt-");
        const string key = "restore-duplicate-obj.bin";
        await S3Fidelity.PutWithClass(s3, bucket, key, System.Security.Cryptography.RandomNumberGenerator.GetBytes(1024), "GLACIER");
        await S3Fidelity.Restore(s3, bucket, key, 1, "Standard");

        // Positive control: an independent read confirms the first restore actually put the object
        // into an in-progress state -- without this, a rejected second call would be indistinguishable
        // from a target that rejects EVERY RestoreObject call regardless of state.
        var before = await S3Fidelity.RestoreHeader(s3, bucket, key) ?? string.Empty;
        Ensure(before == "ongoing-request=\"true\"", $"expected a restore in progress before the duplicate attempt, got Restore={Py.Repr(before)}");
        var (code, message) = await S3Fidelity.ClientError(
            () => S3Fidelity.Restore(s3, bucket, key, 1, "Standard"),
            "expected this call to be rejected, but it SUCCEEDED");
        Ensure(code == "RestoreAlreadyInProgress", $"expected RestoreAlreadyInProgress for a duplicate restore, got {code}");
        Ensure(message == "Object restore is already in progress", message ?? "None");

        // Effect absent: an independent read at the destination, through a DIFFERENT operation than
        // either RestoreObject call, shows the rejected duplicate left the in-flight restore
        // untouched -- it neither reset, completed, nor otherwise disturbed the single job the first
        // call started.
        var after = await S3Fidelity.RestoreHeader(s3, bucket, key) ?? string.Empty;
        Ensure(after == "ongoing-request=\"true\"", $"expected the original restore to still be in progress after the rejected duplicate, got Restore={Py.Repr(after)}");
    }
}
