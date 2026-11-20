// Translated from behavioral/s3/check_restore_object_temporary_availability_window.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.restore-object-temporary-availability-window</summary>
[Category("AwsEvaluationSuite")]
public sealed class RestoreObjectTemporaryAvailabilityWindow
{
    /// <summary>
    /// A GLACIER object is unreadable until restored; a second in-flight restore is rejected.
    /// The restored copy returns the original bytes, a longer re-restore extends the expiry, and
    /// restoring an active-tier INTELLIGENT_TIERING object fails with InvalidObjectState.
    /// </summary>
    [Test]
    [DisplayName("s3.restore-object-temporary-availability-window")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var bucket = await S3Fidelity.CreateBucket(ctx, "sim2real-behav-");
        const string key = "archived.bin";
        var body = "content that was pushed straight to glacier\n"u8.ToArray();
        await S3Fidelity.PutWithClass(s3, bucket, key, body, "GLACIER");

        // 1. Unrestored archived object: GetObject is rejected as InvalidObjectState.
        var unrestored = await S3Fidelity.Refusal(() => s3.GetObjectAsync(bucket, key));
        Ensure(
            unrestored == ("InvalidObjectState", "The operation is not valid for the object's storage class"),
            $"expected GetObject on an unrestored GLACIER object to be rejected with InvalidObjectState, got {S3Fidelity.Repr(unrestored)}");

        // 2. Kick off an expedited restore for 1 day of temporary availability.
        await S3Fidelity.Restore(s3, bucket, key, 1, "Expedited");

        // 3. A second restore request while the first is still in flight is rejected.
        var second = await S3Fidelity.Refusal(() => S3Fidelity.Restore(s3, bucket, key, 1, "Expedited"));
        Ensure(second == ("RestoreAlreadyInProgress", "Object restore is already in progress"), $"expected RestoreAlreadyInProgress, got {S3Fidelity.Repr(second)}");

        // 4. Expedited retrieval typically finishes in 1-5 minutes, hence the 6-minute bound.
        async Task<string> Header() => await S3Fidelity.RestoreHeader(s3, bucket, key) ?? string.Empty;
        var restored = await PollUntil(Header, h => h.Contains("ongoing-request=\"false\"", StringComparison.Ordinal), 360, 15)
            ?? throw Fail("Glacier expedited restore did not complete within the bounded poll window");
        var firstExpiry = RestoreExpiry(restored);

        // 5. Once restored, GetObject succeeds and returns the exact original bytes.
        using (var got = await s3.GetObjectAsync(bucket, key))
        {
            var gotBody = await S3Fidelity.ReadBody(got);
            Ensure(gotBody.SequenceEqual(body), $"expected restored bytes {S3Fidelity.Head(body)}, got {S3Fidelity.Head(gotBody)}");
            Ensure(got.StorageClass?.Value == "GLACIER", $"expected the restored object's storage class to remain GLACIER, got {Py.Repr(got.StorageClass?.Value)}");
        }

        // 6. Re-issuing RestoreObject for the already-active temporary copy with a LONGER period
        // succeeds (S3 updates/extends the restoration window in place) -- it is not an error.
        await S3Fidelity.Restore(s3, bucket, key, 5, "Expedited");
        var extended = await PollUntil(Header, h => h.Contains("ongoing-request=\"false\"", StringComparison.Ordinal), 60, 5);
        Ensure(extended is not null, "extended restore never settled back to ongoing-request=false");
        var secondExpiry = RestoreExpiry(extended!);
        Ensure(secondExpiry > firstExpiry, "re-issuing RestoreObject with more Days did not push the expiry out");

        // 7. A new INTELLIGENT_TIERING object starts in the Frequent Access tier, so there is nothing to
        // restore: S3 answers InvalidObjectState (as for STANDARD), not ObjectAlreadyInActiveTierError.
        const string itKey = "never-archived-it.bin";
        await S3Fidelity.PutWithClass(s3, bucket, itKey, "intelligent-tiering object, still in frequent access"u8.ToArray(), "INTELLIGENT_TIERING");
        var activeTier = await S3Fidelity.Refusal(() => S3Fidelity.Restore(s3, bucket, itKey, 1));
        Ensure(
            activeTier == ("InvalidObjectState", "Restore is not allowed for the object's current storage class"),
            $"expected RestoreObject on an already-active-tier INTELLIGENT_TIERING object to be rejected with InvalidObjectState, got {S3Fidelity.Repr(activeTier)}");

        // The rejected call must not leave any restore state behind.
        var itRestore = await S3Fidelity.RestoreHeader(s3, bucket, itKey);
        Ensure(itRestore is null, $"unexpected Restore header on an active-tier object: {Py.Repr(itRestore)}");
    }

    /// <summary>_restore_expiry(header): the expiry-date of an x-amz-restore header value.</summary>
    private static DateTimeOffset RestoreExpiry(string restoreHeader)
    {
        const string marker = "expiry-date=\"";
        var start = restoreHeader.IndexOf(marker, StringComparison.Ordinal) + marker.Length;
        var end = restoreHeader.IndexOf('"', start);
        return DateTimeOffset.Parse(restoreHeader[start..end], System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>_poll_until(probe, predicate, timeout_s, interval_s): the value once predicate holds; null at the deadline.</summary>
    private static async Task<string?> PollUntil(Func<Task<string>> probe, Func<string, bool> predicate, double timeoutSeconds, double intervalSeconds)
    {
        var clock = FidelityTime.StartNew();
        while (clock.Elapsed.TotalSeconds < timeoutSeconds)
        {
            var value = await probe();
            if (predicate(value))
            {
                return value;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(intervalSeconds));
        }

        return null;
    }
}
