// Translated from behavioral/s3/check_object_retention_shorten_governance_without_bypass_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.object-retention-shorten-governance-without-bypass-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class ObjectRetentionShortenGovernanceWithoutBypassRejected
{
    private const string Check = "object-retention-shorten-governance-without-bypass-rejected";

    /// <summary>
    /// Shortening a GOVERNANCE-mode retention period without BypassGovernanceRetention must be rejected
    /// with AccessDenied -- retention can only ever be tightened or bypassed explicitly, never silently
    /// loosened.
    /// </summary>
    [Test]
    [DisplayName("s3.object-retention-shorten-governance-without-bypass-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        const string key = "retention-shorten-test.txt";
        var name = await S3Fidelity.CreateObjectLockBucket(ctx, "sim2real-behav-retnshort-");
        await s3.PutBucketTaggingAsync(new PutBucketTaggingRequest { BucketName = name, TagSet = [new Tag { Key = "sim2real", Value = Check }] });

        await s3.PutObjectAsync(new PutObjectRequest { BucketName = name, Key = key, ContentBody = "retention-shorten-payload" });

        var now = FidelityTime.UtcNow.UtcDateTime;
        var originalUntil = now + TimeSpan.FromDays(2);
        var shortenedUntil = now + TimeSpan.FromHours(1);
        await s3.PutObjectRetentionAsync(new PutObjectRetentionRequest
        {
            BucketName = name,
            Key = key,
            Retention = new ObjectLockRetention { Mode = ObjectLockRetentionMode.Governance, RetainUntilDate = originalUntil },
        });

        var (code, message) = await S3Fidelity.ClientError(
            () => s3.PutObjectRetentionAsync(new PutObjectRetentionRequest
            {
                BucketName = name,
                Key = key,
                Retention = new ObjectLockRetention { Mode = ObjectLockRetentionMode.Governance, RetainUntilDate = shortenedUntil },
            }),
            "the call was expected to fail and it succeeded");
        Ensure(code == "AccessDenied", $"expected AccessDenied shortening a GOVERNANCE RetainUntilDate without bypass, got {code}");
        Ensure(message == "Access Denied because object protected by object lock.", $"unexpected message {Py.Repr(message)}");

        // Effect absent: the rejected attempt must not have moved the retention date at all.
        var unchanged = (await s3.GetObjectRetentionAsync(new GetObjectRetentionRequest { BucketName = name, Key = key })).Retention;
        Ensure(unchanged.Mode == "GOVERNANCE", $"the rejected shorten attempt changed the retention mode to {Py.Repr(unchanged.Mode?.Value)}");
        Ensure(
            S3Fidelity.Utc(unchanged.RetainUntilDate) >= originalUntil - TimeSpan.FromSeconds(5),
            $"the rejected shorten attempt moved RetainUntilDate to {unchanged.RetainUntilDate:O}, want it unchanged at (or after) {originalUntil:O}");

        // With BypassGovernanceRetention explicitly set, the identical shorten now succeeds.
        await s3.PutObjectRetentionAsync(new PutObjectRetentionRequest
        {
            BucketName = name,
            Key = key,
            Retention = new ObjectLockRetention { Mode = ObjectLockRetentionMode.Governance, RetainUntilDate = shortenedUntil },
            BypassGovernanceRetention = true,
        });
        var got = (await s3.GetObjectRetentionAsync(new GetObjectRetentionRequest { BucketName = name, Key = key })).Retention;
        Ensure(got.Mode == "GOVERNANCE", $"{{'Mode': {Py.Repr(got.Mode?.Value)}, 'RetainUntilDate': {got.RetainUntilDate:O}}}");
        Ensure(
            Math.Abs((S3Fidelity.Utc(got.RetainUntilDate) - shortenedUntil).TotalSeconds) <= 5,
            $"the bypassed shorten left RetainUntilDate at {got.RetainUntilDate:O}, want it near the requested {shortenedUntil:O}");
    }
}
