// Translated from behavioral/s3/check_bucket_abac_invalid_status_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.bucket-abac-invalid-status-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class BucketAbacInvalidStatusRejected
{
    /// <summary>
    /// OVERLY-PERMISSIVE GUARD: PutBucketAbac with a Status value outside the Enabled/Disabled enum MUST
    /// be rejected, not silently accepted/coerced, and the bucket's ABAC status must remain unchanged by
    /// the rejected call.
    /// </summary>
    [Test]
    [DisplayName("s3.bucket-abac-invalid-status-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var bucket = await S3Fidelity.CreateBucket(ctx, "sim2real-behav-bkt-");
        var before = await Status(s3, bucket);

        var (code, message) = await S3Fidelity.ErrorPair(
            () => Put(s3, bucket, "NotARealValue"),
            "expected the call to be rejected, but it SUCCEEDED");
        Ensure(code == "MalformedXML", $"expected MalformedXML for an invalid ABAC Status, got {Py.Repr(code)}");
        const string expected = "The XML you provided was not well-formed or did not validate against our published schema";
        Ensure(message == expected, $"expected message {Py.Repr(expected)}, got {Py.Repr(message)}");

        // effect absent: the rejected call must not have changed the bucket's ABAC status
        var after = await Status(s3, bucket);
        Ensure(after == before, $"ABAC status changed from {Py.Repr(before)} to {Py.Repr(after)} despite rejection");

        // positive control: a valid Status value on the same bucket is accepted and takes effect,
        // so the rejection above is specific to the bad value, not a blanket denial
        var other = before == "Enabled" ? "Disabled" : "Enabled";
        await Put(s3, bucket, other);
        var got = await Status(s3, bucket);
        Ensure(got == other, $"expected valid Status {Py.Repr(other)} to take effect, got {Py.Repr(got)}");
    }

    private static async Task<string?> Status(IAmazonS3 s3, string bucket) =>
        (await s3.GetBucketAbacAsync(new GetBucketAbacRequest { BucketName = bucket })).AbacStatus.Status?.Value;

    private static Task<PutBucketAbacResponse> Put(IAmazonS3 s3, string bucket, string status) =>
        s3.PutBucketAbacAsync(new PutBucketAbacRequest { BucketName = bucket, AbacStatus = new AbacStatus { Status = status } });
}
