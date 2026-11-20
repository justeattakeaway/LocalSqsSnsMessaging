// Translated from behavioral/s3/check_object_retention_governance_bypass_delete.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.object-retention-governance-bypass-delete</summary>
[Category("AwsEvaluationSuite")]
public sealed class ObjectRetentionGovernanceBypassDelete
{
    /// <summary>
    /// A GOVERNANCE-mode retained version cannot be deleted without BypassGovernanceRetention
    /// (AccessDenied), but CAN be deleted with it set.
    /// </summary>
    [Test]
    [DisplayName("s3.object-retention-governance-bypass-delete")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var bucket = await S3Fidelity.CreateObjectLockBucket(ctx);

        // Positive control: an unretained version in the same bucket deletes normally, so the
        // AccessDenied below cannot be a target that rejects every DeleteObject call.
        const string controlKey = "unretained.txt";
        var controlVersion = (await s3.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = controlKey, ContentBody = "unretained" })).VersionId;
        await s3.DeleteObjectAsync(new DeleteObjectRequest { BucketName = bucket, Key = controlKey, VersionId = controlVersion });
        var (controlGone, controlMessage) = await Err(() => s3.GetObjectAsync(new GetObjectRequest { BucketName = bucket, Key = controlKey, VersionId = controlVersion }));
        Ensure(controlGone == "NoSuchVersion", $"expected the unretained control delete to stick, got {controlGone}");
        Ensure(controlMessage == "The specified version does not exist.", $"unexpected message {Py.Repr(controlMessage)}");

        const string key = "governance.txt";
        var versionId = (await s3.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = key, ContentBody = "gov-protected" })).VersionId;

        var retainUntil = FidelityTime.UtcNow.UtcDateTime + TimeSpan.FromMinutes(30);
        await s3.PutObjectRetentionAsync(new PutObjectRetentionRequest
        {
            BucketName = bucket,
            Key = key,
            Retention = new ObjectLockRetention { Mode = ObjectLockRetentionMode.Governance, RetainUntilDate = retainUntil },
        });
        var retention = (await s3.GetObjectRetentionAsync(new GetObjectRetentionRequest { BucketName = bucket, Key = key, VersionId = versionId })).Retention;
        Ensure(retention.Mode == "GOVERNANCE", $"retention did not land, got {{'Mode': {Py.Repr(retention.Mode?.Value)}}}");

        var (code, message) = await Err(() => s3.DeleteObjectAsync(new DeleteObjectRequest { BucketName = bucket, Key = key, VersionId = versionId }));
        Ensure(code == "AccessDenied", $"expected AccessDenied deleting GOVERNANCE-retained version without bypass, got {code}");
        Ensure(message == "Access Denied because object protected by object lock.", $"unexpected message {Py.Repr(message)}");

        // Effect absent: the refused delete did not touch the version.
        using (var get = await s3.GetObjectAsync(new GetObjectRequest { BucketName = bucket, Key = key, VersionId = versionId }))
        {
            var intact = await S3Fidelity.ReadBody(get);
            Ensure(intact.AsSpan().SequenceEqual("gov-protected"u8), $"refused delete altered the object body, got {Py.Repr(intact)}");
        }

        // With BypassGovernanceRetention=True (and the caller's implicit permission), it succeeds.
        await s3.DeleteObjectAsync(new DeleteObjectRequest { BucketName = bucket, Key = key, VersionId = versionId, BypassGovernanceRetention = true });

        // Terminal independent read: the bypassed delete actually removed the version.
        var (goneCode, goneMessage) = await Err(() => s3.GetObjectAsync(new GetObjectRequest { BucketName = bucket, Key = key, VersionId = versionId }));
        Ensure(goneCode == "NoSuchVersion", $"expected NoSuchVersion after bypass delete, got {goneCode}");
        Ensure(goneMessage == "The specified version does not exist.", $"unexpected message {Py.Repr(goneMessage)}");
    }

    /// <summary>The (Error.Code, Error.Message) of fn(); RAISES if fn succeeds.</summary>
    private static Task<(string? Code, string? Message)> Err(Func<Task> fn) =>
        S3Fidelity.ClientError(fn, "the call was expected to fail and it succeeded");
}
