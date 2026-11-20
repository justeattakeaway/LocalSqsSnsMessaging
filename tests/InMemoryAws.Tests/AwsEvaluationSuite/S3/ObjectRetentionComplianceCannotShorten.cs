// Translated from behavioral/s3/check_object_retention_compliance_cannot_shorten.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.object-retention-compliance-cannot-shorten</summary>
[Category("AwsEvaluationSuite")]
public sealed class ObjectRetentionComplianceCannotShorten
{
    private const int RetainS = 75;

    /// <summary>
    /// Once a version is under COMPLIANCE retention, its RetainUntilDate can only be extended, never
    /// shortened - attempting to shorten it MUST fail.
    /// </summary>
    [Test]
    [DisplayName("s3.object-retention-compliance-cannot-shorten")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var bucket = await S3Fidelity.CreateObjectLockBucket(ctx);
        const string key = "compliance.txt";
        await s3.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = key, ContentBody = "compliance-protected" });

        var now = FidelityTime.UtcNow.UtcDateTime;
        var farFuture = now + TimeSpan.FromSeconds(RetainS);
        await s3.PutObjectRetentionAsync(new PutObjectRetentionRequest
        {
            BucketName = bucket,
            Key = key,
            Retention = new ObjectLockRetention { Mode = ObjectLockRetentionMode.Compliance, RetainUntilDate = farFuture },
        });

        var sooner = now + TimeSpan.FromSeconds(20);

        Task Shorten() => s3.PutObjectRetentionAsync(new PutObjectRetentionRequest
        {
            BucketName = bucket,
            Key = key,
            Retention = new ObjectLockRetention { Mode = ObjectLockRetentionMode.Compliance, RetainUntilDate = sooner },
        });

        var (code, message) = await S3Fidelity.ClientError(Shorten, "the call was expected to fail and it succeeded");
        Ensure(code == "AccessDenied", $"expected AccessDenied shortening a COMPLIANCE RetainUntilDate, got {code}");
        Ensure(message == "Access Denied because object protected by object lock.", $"unexpected message {Py.Repr(message)}");

        var fetched = (await s3.GetObjectRetentionAsync(new GetObjectRetentionRequest { BucketName = bucket, Key = key })).Retention;
        Ensure(
            fetched.Mode == "COMPLIANCE",
            $"expected retention Mode to remain COMPLIANCE after the rejected shorten, got {Py.Repr(fetched.Mode?.Value)}");

        // Retention date must remain at (or beyond) the originally-set far-future date.
        Ensure(
            S3Fidelity.Utc(fetched.RetainUntilDate) >= farFuture - TimeSpan.FromSeconds(5),
            $"expected RetainUntilDate >= {farFuture:O} (the rejected shorten must not have taken effect), got {fetched.RetainUntilDate:O}");
    }
}
