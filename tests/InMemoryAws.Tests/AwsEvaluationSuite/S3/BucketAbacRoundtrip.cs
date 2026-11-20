// Translated from behavioral/s3/check_bucket_abac_roundtrip.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.bucket-abac-roundtrip</summary>
[Category("AwsEvaluationSuite")]
public sealed class BucketAbacRoundtrip
{
    /// <summary>
    /// PutBucketAbac sets the attribute-based-access-control status for a bucket and GetBucketAbac
    /// returns it unchanged; toggling Enabled -> Disabled roundtrips too.
    /// </summary>
    [Test]
    [DisplayName("s3.bucket-abac-roundtrip")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var name = await S3Fidelity.CreateBucket(ctx, "sim2real-behav-abac-");

        var initial = await s3.GetBucketAbacAsync(new GetBucketAbacRequest { BucketName = name });
        Ensure(initial.AbacStatus.Status?.Value == "Disabled", Py.Repr(initial.AbacStatus.Status?.Value));

        await s3.PutBucketAbacAsync(new PutBucketAbacRequest { BucketName = name, AbacStatus = new AbacStatus { Status = "Enabled" } });
        var got = await s3.GetBucketAbacAsync(new GetBucketAbacRequest { BucketName = name });
        Ensure(got.AbacStatus.Status?.Value == "Enabled", Py.Repr(got.AbacStatus.Status?.Value));

        await s3.PutBucketAbacAsync(new PutBucketAbacRequest { BucketName = name, AbacStatus = new AbacStatus { Status = "Disabled" } });
        var got2 = await s3.GetBucketAbacAsync(new GetBucketAbacRequest { BucketName = name });
        Ensure(got2.AbacStatus.Status?.Value == "Disabled", Py.Repr(got2.AbacStatus.Status?.Value));
    }
}
