// Translated from behavioral/s3/check_restore_object_server_side_tier_transition.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.restore-object-server-side-tier-transition</summary>
[Category("AwsEvaluationSuite")]
public sealed class RestoreObjectServerSideTierTransition
{
    /// <summary>
    /// RestoreObject on a GLACIER object drives a server-managed transition from
    /// not-restored -> restore-in-progress -> temporarily-restored, observable purely
    /// through HeadObject/GetObject state with no further client action.
    /// </summary>
    [Test]
    [DisplayName("s3.restore-object-server-side-tier-transition")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var bucket = await S3Fidelity.CreateBucket(ctx, "sim2real-behav-rst");
        await s3.PutBucketTaggingAsync(new PutBucketTaggingRequest { BucketName = bucket, TagSet = [new Tag { Key = "sim2real", Value = "restore-object-server-side-tier-transition" }] });
        const string key = "archived.bin";
        var body = "restore-me-content-0123456789"u8.ToArray();
        await S3Fidelity.PutWithClass(s3, bucket, key, body, "GLACIER");
        var (head, restore) = await S3Fidelity.HeadWithRestore(s3, bucket, key);
        Ensure(head.StorageClass?.Value == "GLACIER", "assert head[\"StorageClass\"] == \"GLACIER\"");
        Ensure(restore is null, "object should have no restore state before RestoreObject");
        var refusal = await S3Fidelity.Refusal(() => s3.GetObjectAsync(bucket, key));
        Ensure(
            refusal == ("InvalidObjectState", "The operation is not valid for the object's storage class"),
            $"GET on un-restored GLACIER object should be InvalidObjectState, got {S3Fidelity.Repr(refusal)}");
        if (await FidelityContext.CaptureError(() => S3Fidelity.Restore(s3, bucket, key, 1, "Expedited")) is { } error)
        {
            throw Fail($"RestoreObject rejected (capacity/quota?): {error}");
        }

        // Immediately after the request, S3 should report restoration as in-progress.
        Task<string?> RestoreHeader() => S3Fidelity.RestoreHeader(s3, bucket, key);
        var inProgress = await PollUntil(RestoreHeader, v => v is not null && v.Contains("ongoing-request=\"true\"", StringComparison.Ordinal), 30.0, 2.0);
        if (inProgress is null)
        {
            throw Fail("restore never reported ongoing-request=true within 30s");
        }

        var completed = await PollUntil(RestoreHeader, v => v is not null && v.Contains("ongoing-request=\"false\"", StringComparison.Ordinal), 240.0, 15.0);
        if (completed is null)
        {
            throw Fail(
                "GLACIER expedited restore did not complete within the 4-minute bounded " +
                "poll window; real AWS retrieval time can occasionally exceed test budget. " +
                "(Capped below the suite's 300s body budget in record_ground_truth.py's " +
                "_TIMEOUT table -- a longer window here would just be SIGKILLed uncleanly.)");
        }

        Ensure(completed.Contains("expiry-date", StringComparison.Ordinal), $"completed restore missing expiry-date: {completed}");

        // No further client action required: GetObject now succeeds directly.
        using var got = await s3.GetObjectAsync(bucket, key);
        Ensure((await S3Fidelity.ReadBody(got)).SequenceEqual(body), "assert got[\"Body\"].read() == body");
        Ensure(got.StorageClass?.Value == "GLACIER", "assert got[\"StorageClass\"] == \"GLACIER\"  # storage class is unchanged; only accessibility changes");
    }

    /// <summary>_poll_until(probe, predicate, timeout_s, interval_s): the value once predicate holds, else null at the deadline.</summary>
    private static async Task<string?> PollUntil(Func<Task<string?>> probe, Func<string?, bool> predicate, double timeoutSeconds, double intervalSeconds)
    {
        var clock = FidelityTime.StartNew();
        var value = await probe();
        while (clock.Elapsed.TotalSeconds < timeoutSeconds)
        {
            if (predicate(value))
            {
                return value;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(intervalSeconds));
            value = await probe();
        }

        return predicate(value) ? value : null;
    }
}
