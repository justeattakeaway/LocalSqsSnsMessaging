// Translated from behavioral/s3/check_object_retention_lifecycle.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.object-retention-lifecycle</summary>
[Category("AwsEvaluationSuite")]
public sealed class ObjectRetentionLifecycle
{
    /// <summary>
    /// PutObjectRetention applies a retain-until-date/mode to an object and GetObjectRetention
    /// reflects the applied hold state exactly.
    /// </summary>
    [Test]
    [DisplayName("s3.object-retention-lifecycle")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        const string key = "retention-test.txt";
        var name = await S3Fidelity.CreateObjectLockBucket(ctx, "sim2real-behav-retn-");

        await s3.PutObjectAsync(new PutObjectRequest { BucketName = name, Key = key, ContentBody = "retention-payload" });

        var retainUntil = FidelityTime.UtcNow.UtcDateTime + TimeSpan.FromHours(6);
        await s3.PutObjectRetentionAsync(new PutObjectRetentionRequest
        {
            BucketName = name,
            Key = key,
            Retention = new ObjectLockRetention { Mode = ObjectLockRetentionMode.Governance, RetainUntilDate = retainUntil },
        });

        var got = await s3.GetObjectRetentionAsync(new GetObjectRetentionRequest { BucketName = name, Key = key });
        var retention = got.Retention;
        Ensure(retention.Mode == "GOVERNANCE", $"{{'Mode': {Py.Repr(retention.Mode?.Value)}, 'RetainUntilDate': {retention.RetainUntilDate:O}}}");
        var delta = Math.Abs((S3Fidelity.Utc(retention.RetainUntilDate) - retainUntil).TotalSeconds);
        Ensure(delta < 5, $"RetainUntilDate drifted by {delta}s: {retention.RetainUntilDate:O} vs {retainUntil:O}");
    }
}
