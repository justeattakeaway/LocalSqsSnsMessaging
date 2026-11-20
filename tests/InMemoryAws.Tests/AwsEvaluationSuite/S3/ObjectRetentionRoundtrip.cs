// Translated from behavioral/s3/check_object_retention_roundtrip.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.object-retention-roundtrip</summary>
[Category("AwsEvaluationSuite")]
public sealed class ObjectRetentionRoundtrip
{
    /// <summary>
    /// PutObjectRetention settings are reflected by GetObjectRetention, and an extension of
    /// RetainUntilDate (allowed under GOVERNANCE with bypass) is also reflected.
    /// </summary>
    [Test]
    [DisplayName("s3.object-retention-roundtrip")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var name = await S3Fidelity.CreateObjectLockBucket(ctx);
        const string key = "retained.txt";
        await s3.PutObjectAsync(new PutObjectRequest { BucketName = name, Key = key, ContentBody = "data" });

        var retainUntil = FidelityTime.UtcNow.UtcDateTime + TimeSpan.FromMinutes(20);
        await s3.PutObjectRetentionAsync(new PutObjectRetentionRequest
        {
            BucketName = name,
            Key = key,
            Retention = new ObjectLockRetention { Mode = ObjectLockRetentionMode.Governance, RetainUntilDate = retainUntil },
        });
        var fetched = (await s3.GetObjectRetentionAsync(new GetObjectRetentionRequest { BucketName = name, Key = key })).Retention;
        Ensure(fetched.Mode == "GOVERNANCE", $"expected Mode GOVERNANCE, got {fetched.Mode?.Value}");
        Ensure(
            Math.Abs((S3Fidelity.Utc(fetched.RetainUntilDate) - retainUntil).TotalSeconds) < 5,
            $"expected RetainUntilDate ~= {retainUntil:O}, got {fetched.RetainUntilDate:O}");

        var extended = retainUntil + TimeSpan.FromMinutes(20);
        await s3.PutObjectRetentionAsync(new PutObjectRetentionRequest
        {
            BucketName = name,
            Key = key,
            Retention = new ObjectLockRetention { Mode = ObjectLockRetentionMode.Governance, RetainUntilDate = extended },
            BypassGovernanceRetention = true,
        });
        var fetched2 = (await s3.GetObjectRetentionAsync(new GetObjectRetentionRequest { BucketName = name, Key = key })).Retention;
        Ensure(
            Math.Abs((S3Fidelity.Utc(fetched2.RetainUntilDate) - extended).TotalSeconds) < 5,
            $"expected extended RetainUntilDate ~= {extended:O}, got {fetched2.RetainUntilDate:O}");
    }
}
