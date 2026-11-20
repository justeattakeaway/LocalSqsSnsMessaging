// Translated from behavioral/s3/check_restore_object_glacier_transition.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.restore-object-glacier-transition</summary>
[Category("AwsEvaluationSuite")]
public sealed class RestoreObjectGlacierTransition
{
    /// <summary>
    /// RestoreObject transitions a GLACIER object from unreadable -> ongoing-restore ->
    /// temporarily retrievable via GetObject/CopyObject.
    /// </summary>
    [Test]
    [DisplayName("s3.restore-object-glacier-transition")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var name = await S3Fidelity.CreateBucket(ctx, "sim2real-behav-bkt-");
        const string key = "archived.txt";
        var body = "cold-data"u8.ToArray();
        await S3Fidelity.PutWithClass(s3, name, key, body, "GLACIER");
        var (head, restore) = await S3Fidelity.HeadWithRestore(s3, name, key);
        Ensure(head.StorageClass?.Value == "GLACIER", $"expected GLACIER storage class, got {Py.Repr(head.StorageClass?.Value)}");
        Ensure(restore is null, $"unrestored object should have no Restore header, got {Py.Repr(restore)}");
        var refusal = await S3Fidelity.Refusal(() => s3.GetObjectAsync(name, key));
        Ensure(
            refusal == ("InvalidObjectState", "The operation is not valid for the object's storage class"),
            $"expected InvalidObjectState for an unrestored object, got {S3Fidelity.Repr(refusal)}");
        if (await FidelityContext.CaptureError(() => S3Fidelity.Restore(s3, name, key, 1, "Expedited")) is { } error)
        {
            throw Fail($"RestoreObject rejected (transient expedited capacity?): {error}");
        }

        var restoreHdr = await S3Fidelity.RestoreHeader(s3, name, key) ?? string.Empty;
        Ensure(restoreHdr.Contains("ongoing-request=\"true\"", StringComparison.Ordinal), $"expected an ongoing-restore marker, got {Py.Repr(restoreHdr)}");
        await PollUntil(
            async () => await S3Fidelity.RestoreHeader(s3, name, key) ?? string.Empty,
            hdr => hdr.Contains("ongoing-request=\"false\"", StringComparison.Ordinal),
            timeoutSeconds: 240,
            intervalSeconds: 10,
            description: "GLACIER expedited restore completion",
            onTimeout: "restore did not complete within the bounded poll window (real-AWS " +
                "expedited retrieval latency, typically 1-5 min, exceeded it)");
        var restored = await S3Fidelity.GetBytes(s3, name, key);
        Ensure(restored.SequenceEqual(body), "restored object must be readable with its original bytes");
        const string destKey = "archived-copy.txt";
        await s3.CopyObjectAsync(new CopyObjectRequest { SourceBucket = name, SourceKey = key, DestinationBucket = name, DestinationKey = destKey });
        var copied = await S3Fidelity.GetBytes(s3, name, destKey);
        Ensure(copied.SequenceEqual(body), "CopyObject must work against the temporarily-restored object");
    }

    /// <summary>_poll_until(fn, pred, timeout_s, interval_s, description): polls fn until pred holds; fails on timeout.</summary>
    private static async Task<string> PollUntil(
        Func<Task<string>> fn, Func<string, bool> pred, double timeoutSeconds, double intervalSeconds, string description, string onTimeout)
    {
        var clock = FidelityTime.StartNew();
        string? last = null;
        while (clock.Elapsed.TotalSeconds < timeoutSeconds)
        {
            last = await fn();
            if (pred(last))
            {
                return last;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(intervalSeconds));
        }

        // poll timed out after {timeout_s}s: {description} (last={last!r}), re-raised as:
        throw Fail($"{onTimeout} (poll timed out after {timeoutSeconds}s: {description} (last={Py.Repr(last)}))");
    }
}
