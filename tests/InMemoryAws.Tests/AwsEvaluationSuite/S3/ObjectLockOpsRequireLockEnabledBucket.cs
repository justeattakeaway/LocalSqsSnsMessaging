// Translated from behavioral/s3/check_object_lock_ops_require_lock_enabled_bucket.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.object-lock-ops-require-lock-enabled-bucket</summary>
[Category("AwsEvaluationSuite")]
public sealed class ObjectLockOpsRequireLockEnabledBucket
{
    private const string NotLockEnabledMessage = "Bucket is missing Object Lock Configuration";

    /// <summary>
    /// PutObjectLegalHold and PutObjectRetention are rejected on a bucket created without Object Lock
    /// but succeed on a lock-enabled twin; reads on the unlocked bucket show nothing stored.
    /// </summary>
    [Test]
    [DisplayName("s3.object-lock-ops-require-lock-enabled-bucket")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        const string key = "obj.txt";
        var retainUntil = FidelityTime.UtcNow.UtcDateTime + TimeSpan.FromHours(6);

        var locked = await S3Fidelity.CreateObjectLockBucket(ctx, "sim2real-behav-bkt-");
        var unlocked = await S3Fidelity.CreateBucket(ctx, "sim2real-behav-bkt-"); // no ObjectLockEnabledForBucket
        foreach (var name in new[] { locked, unlocked })
        {
            await s3.PutObjectAsync(new PutObjectRequest { BucketName = name, Key = key, ContentBody = "data" });
        }

        // positive control: the identical calls succeed on the lock-enabled twin
        await s3.PutObjectLegalHoldAsync(new PutObjectLegalHoldRequest { BucketName = locked, Key = key, LegalHold = new ObjectLockLegalHold { Status = ObjectLockLegalHoldStatus.On } });
        var held = await s3.GetObjectLegalHoldAsync(new GetObjectLegalHoldRequest { BucketName = locked, Key = key });
        Ensure(held.LegalHold.Status == "ON", $"{{'LegalHold': {{'Status': {Py.Repr(held.LegalHold.Status?.Value)}}}}}");
        await s3.PutObjectLegalHoldAsync(new PutObjectLegalHoldRequest { BucketName = locked, Key = key, LegalHold = new ObjectLockLegalHold { Status = ObjectLockLegalHoldStatus.Off } });

        await s3.PutObjectRetentionAsync(new PutObjectRetentionRequest
        {
            BucketName = locked,
            Key = key,
            Retention = new ObjectLockRetention { Mode = ObjectLockRetentionMode.Governance, RetainUntilDate = retainUntil },
        });
        var retention = await s3.GetObjectRetentionAsync(new GetObjectRetentionRequest { BucketName = locked, Key = key });
        Ensure(retention.Retention.Mode == "GOVERNANCE", $"{{'Retention': {{'Mode': {Py.Repr(retention.Retention.Mode?.Value)}}}}}");

        // negative: both calls are rejected, with the specific error code, on the non-lock-enabled bucket
        var (code, message) = await Err(() => s3.PutObjectLegalHoldAsync(new PutObjectLegalHoldRequest
        {
            BucketName = unlocked,
            Key = key,
            LegalHold = new ObjectLockLegalHold { Status = ObjectLockLegalHoldStatus.On },
        }));
        Ensure(code == "InvalidRequest", $"expected InvalidRequest for PutObjectLegalHold, got {Py.Repr(code)}");
        Ensure(message == NotLockEnabledMessage, $"unexpected PutObjectLegalHold message: {Py.Repr(message)}");

        (code, message) = await Err(() => s3.PutObjectRetentionAsync(new PutObjectRetentionRequest
        {
            BucketName = unlocked,
            Key = key,
            Retention = new ObjectLockRetention { Mode = ObjectLockRetentionMode.Governance, RetainUntilDate = retainUntil },
        }));
        Ensure(code == "InvalidRequest", $"expected InvalidRequest for PutObjectRetention, got {Py.Repr(code)}");
        Ensure(message == NotLockEnabledMessage, $"unexpected PutObjectRetention message: {Py.Repr(message)}");

        // effect absent: independent reads on the unlocked bucket confirm nothing stuck despite the rejected writes
        (code, message) = await Err(() => s3.GetObjectLegalHoldAsync(new GetObjectLegalHoldRequest { BucketName = unlocked, Key = key }));
        Ensure(code == "InvalidRequest", $"expected InvalidRequest reading legal hold, got {Py.Repr(code)}");
        Ensure(message == NotLockEnabledMessage, $"unexpected GetObjectLegalHold message: {Py.Repr(message)}");

        (code, message) = await Err(() => s3.GetObjectRetentionAsync(new GetObjectRetentionRequest { BucketName = unlocked, Key = key }));
        Ensure(code == "InvalidRequest", $"expected InvalidRequest reading retention, got {Py.Repr(code)}");
        Ensure(message == NotLockEnabledMessage, $"unexpected GetObjectRetention message: {Py.Repr(message)}");
    }

    /// <summary>
    /// Call fn(); return the (code, message) of the ClientError it raises. An unexpected success raises,
    /// so a target that accepts what AWS refuses cannot read as a code mismatch.
    /// </summary>
    private static Task<(string? Code, string? Message)> Err(Func<Task> fn) =>
        S3Fidelity.ClientError(fn, "expected a ClientError, but the call SUCCEEDED");
}
