// Translated from behavioral/s3/check_get_object_invalid_state_archived.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.get-object-invalid-state-archived</summary>
[Category("AwsEvaluationSuite")]
public sealed class GetObjectInvalidStateArchived
{
    /// <summary>
    /// GetObject on an archived (GLACIER), not-yet-restored object returns
    /// InvalidObjectState rather than the object body -- but only for the archived
    /// object; an ordinary STANDARD object in the same bucket must still read fine.
    /// </summary>
    [Test]
    [DisplayName("s3.get-object-invalid-state-archived")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var bucket = await S3Fidelity.CreateBucket(ctx, "sim2real-behav-");

        // Positive control: GetObject on a normal object in this bucket must succeed,
        // so a target that rejects every read cannot pass by accident.
        const string liveKey = "live-object.bin";
        var liveBody = "warm-data"u8.ToArray();
        await S3Fidelity.PutWithClass(s3, bucket, liveKey, liveBody);
        ctx.OnCleanup(() => s3.DeleteObjectAsync(bucket, liveKey));
        Ensure((await S3Fidelity.GetBytes(s3, bucket, liveKey)).SequenceEqual(liveBody), "positive control: a STANDARD object must read back unchanged");
        const string key = "archived-object.bin";
        await S3Fidelity.PutWithClass(s3, bucket, key, "deep-freeze"u8.ToArray(), "GLACIER");
        ctx.OnCleanup(() => s3.DeleteObjectAsync(bucket, key));
        var (head, restore) = await S3Fidelity.HeadWithRestore(s3, bucket, key);
        Ensure(head.StorageClass?.Value == "GLACIER", $"expected StorageClass GLACIER, got {Py.Repr(head.StorageClass?.Value)}");
        Ensure(restore is null, $"expected no Restore header before any restore request, got {Py.Repr(restore)}");
        var err = await S3Fidelity.Refusal(() => s3.GetObjectAsync(bucket, key));
        Ensure(err.Code == "InvalidObjectState", $"expected InvalidObjectState reading an un-restored GLACIER object, got {S3Fidelity.Repr(err)}");
        Ensure(err.Message == "The operation is not valid for the object's storage class", S3Fidelity.Repr(err));
    }
}
