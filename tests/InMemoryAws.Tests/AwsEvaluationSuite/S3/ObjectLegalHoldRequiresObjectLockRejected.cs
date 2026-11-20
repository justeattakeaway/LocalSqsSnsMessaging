// Translated from behavioral/s3/check_object_legal_hold_requires_object_lock_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.object-legal-hold-requires-object-lock-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class ObjectLegalHoldRequiresObjectLockRejected
{
    private const string NotLockEnabledMessage = "Bucket is missing Object Lock Configuration";

    /// <summary>
    /// PutObjectLegalHold on a bucket created without Object Lock is rejected with InvalidRequest and
    /// its exact message, while the same call on a twin Object-Lock-enabled bucket sets the hold ON.
    /// </summary>
    [Test]
    [DisplayName("s3.object-legal-hold-requires-object-lock-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        const string key = "legal-hold-negative-test.txt";
        var name = await S3Fidelity.CreateBucket(ctx, "sim2real-behav-legalholdneg-"); // note: no ObjectLockEnabledForBucket

        await s3.PutObjectAsync(new PutObjectRequest { BucketName = name, Key = key, ContentBody = "no-object-lock-payload" });

        var (code, message) = await Error(() => s3.PutObjectLegalHoldAsync(new PutObjectLegalHoldRequest
        {
            BucketName = name,
            Key = key,
            LegalHold = new ObjectLockLegalHold { Status = ObjectLockLegalHoldStatus.On },
        }));
        Ensure(code == "InvalidRequest", Py.Repr(code));
        Ensure(message == NotLockEnabledMessage, $"unexpected InvalidRequest message: {Py.Repr(message)}");

        // GetObjectLegalHold on the same bucket errors out too (no lock configuration
        // at all) -- real S3 reports this the same way as the Put call, as
        // InvalidRequest, since the bucket itself was never Object-Lock-enabled
        // (NoSuchObjectLockConfiguration is what get_object_lock_configuration
        // returns for a *different*, lock-enabled bucket that lacks a configured
        // default retention, which does not apply here).
        var (code2, message2) = await Error(() => s3.GetObjectLegalHoldAsync(new GetObjectLegalHoldRequest { BucketName = name, Key = key }));
        Ensure(code2 == "InvalidRequest", Py.Repr(code2));
        Ensure(message2 == NotLockEnabledMessage, $"unexpected InvalidRequest message: {Py.Repr(message2)}");

        // Positive control: the identical calls succeed on a bucket that IS
        // Object-Lock-enabled, so the rejection above is provably tied to the missing
        // lock configuration rather than to the operation being unconditionally refused.
        var lockName = await S3Fidelity.CreateObjectLockBucket(ctx, "sim2real-behav-legalholdpos-");

        await s3.PutObjectAsync(new PutObjectRequest { BucketName = lockName, Key = key, ContentBody = "has-object-lock-payload" });
        await s3.PutObjectLegalHoldAsync(new PutObjectLegalHoldRequest
        {
            BucketName = lockName,
            Key = key,
            LegalHold = new ObjectLockLegalHold { Status = ObjectLockLegalHoldStatus.On },
        });
        var held = (await s3.GetObjectLegalHoldAsync(new GetObjectLegalHoldRequest { BucketName = lockName, Key = key })).LegalHold;
        Ensure(held.Status == "ON", $"expected legal hold ON on a lock-enabled bucket, got {{'Status': {Py.Repr(held.Status?.Value)}}}");
    }

    /// <summary>
    /// Call fn(); return the (code, message) of the ClientError it raises. An unexpected success raises,
    /// so a target that accepts what AWS refuses cannot read as a code mismatch.
    /// </summary>
    private static Task<(string? Code, string? Message)> Error(Func<Task> fn) =>
        S3Fidelity.ClientError(fn, "expected a ClientError, but the call SUCCEEDED");
}
