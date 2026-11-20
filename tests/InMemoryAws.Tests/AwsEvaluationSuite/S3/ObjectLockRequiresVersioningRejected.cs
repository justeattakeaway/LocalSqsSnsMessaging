// Translated from behavioral/s3/check_object_lock_requires_versioning_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.object-lock-requires-versioning-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class ObjectLockRequiresVersioningRejected
{
    private const string VersioningRequiredMessage = "Versioning must be 'Enabled' on the bucket to apply a Object Lock configuration";
    private const string NoLockConfigurationMessage = "Object Lock configuration does not exist for this bucket";

    /// <summary>
    /// Object Lock requires versioning: PutObjectLockConfiguration on a non-versioned bucket must fail
    /// with InvalidBucketState, nothing is persisted by the rejected call, and enabling versioning
    /// lifts the rejection so the identical call now succeeds.
    /// </summary>
    [Test]
    [DisplayName("s3.object-lock-requires-versioning-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var name = await S3Fidelity.CreateBucket(ctx, "sim2real-behav-bkt-");
        Ensure((await s3.GetBucketVersioningAsync(new GetBucketVersioningRequest { BucketName = name })).VersioningConfig?.Status is null or { Value: "Off" }, "precondition: bucket must be unversioned");
        PutObjectLockConfigurationRequest LockCfg() => new()
        {
            BucketName = name,
            ObjectLockConfiguration = new ObjectLockConfiguration { ObjectLockEnabled = ObjectLockEnabled.Enabled },
        };

        var (code, message) = await Err(() => s3.PutObjectLockConfigurationAsync(LockCfg()));
        Ensure(code == "InvalidBucketState", $"expected InvalidBucketState on an unversioned bucket, got {Py.Repr(code)}");
        Ensure(message == VersioningRequiredMessage, $"unexpected InvalidBucketState message: {Py.Repr(message)}");

        // nothing may have been persisted by the rejected call
        (code, message) = await Err(() => s3.GetObjectLockConfigurationAsync(new GetObjectLockConfigurationRequest { BucketName = name }));
        Ensure(code == "ObjectLockConfigurationNotFoundError", $"expected ObjectLockConfigurationNotFoundError, got {Py.Repr(code)}");
        Ensure(message == NoLockConfigurationMessage, $"unexpected ObjectLockConfigurationNotFoundError message: {Py.Repr(message)}");

        // restore: enabling versioning lifts the rejection -- the identical call now succeeds
        await s3.PutBucketVersioningAsync(new PutBucketVersioningRequest
        {
            BucketName = name,
            VersioningConfig = new S3BucketVersioningConfig { Status = VersionStatus.Enabled },
        });
        await s3.PutObjectLockConfigurationAsync(LockCfg());

        var got = (await s3.GetObjectLockConfigurationAsync(new GetObjectLockConfigurationRequest { BucketName = name })).ObjectLockConfiguration;
        Ensure(
            got.ObjectLockEnabled == "Enabled",
            $"expected the identical PutObjectLockConfiguration call to succeed once versioning is enabled, got {{'ObjectLockEnabled': {Py.Repr(got.ObjectLockEnabled?.Value)}}}");
    }

    /// <summary>
    /// Call fn(); return the (code, message) of the ClientError it raises. An unexpected success raises,
    /// so a target that accepts what AWS refuses cannot read as a code mismatch.
    /// </summary>
    private static Task<(string? Code, string? Message)> Err(Func<Task> fn) =>
        S3Fidelity.ClientError(fn, "expected a ClientError, but the call SUCCEEDED");
}
