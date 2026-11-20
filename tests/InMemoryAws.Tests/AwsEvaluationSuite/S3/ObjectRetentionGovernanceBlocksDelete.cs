// Translated from behavioral/s3/check_object_retention_governance_blocks_delete.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.object-retention-governance-blocks-delete</summary>
[Category("AwsEvaluationSuite")]
public sealed class ObjectRetentionGovernanceBlocksDelete
{
    /// <summary>
    /// DeleteObject on a version under active GOVERNANCE retention must fail without
    /// BypassGovernanceRetention (overly-permissive boundary); the retained version survives the
    /// refusal untouched, and is verified truly gone only once bypass is granted.
    /// </summary>
    [Test]
    [DisplayName("s3.object-retention-governance-blocks-delete")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var name = await S3Fidelity.CreateObjectLockBucket(ctx);

        // Positive control: an unretained version in this same bucket deletes normally, so the
        // AccessDenied below can be attributed to the retention and not to a broken bucket/principal.
        const string controlKey = "unretained.txt";
        var controlVersion = (await s3.PutObjectAsync(new PutObjectRequest { BucketName = name, Key = controlKey, ContentBody = "control" })).VersionId;
        await s3.DeleteObjectAsync(new DeleteObjectRequest { BucketName = name, Key = controlKey, VersionId = controlVersion });
        var (controlCode, controlMessage) = await Err(() => s3.GetObjectAsync(new GetObjectRequest { BucketName = name, Key = controlKey, VersionId = controlVersion }));
        Ensure(controlCode == "NoSuchVersion", $"positive control: deleting an unretained version should leave it gone, got {Py.Repr(controlCode)}");
        Ensure(controlMessage == "The specified version does not exist.", $"unexpected message {Py.Repr(controlMessage)}");

        const string key = "retained.txt";
        var put = await s3.PutObjectAsync(new PutObjectRequest { BucketName = name, Key = key, ContentBody = "locked" });
        var versionId = put.VersionId;

        var retainUntil = FidelityTime.UtcNow.UtcDateTime + TimeSpan.FromMinutes(5);
        await s3.PutObjectRetentionAsync(new PutObjectRetentionRequest
        {
            BucketName = name,
            Key = key,
            VersionId = versionId,
            Retention = new ObjectLockRetention { Mode = ObjectLockRetentionMode.Governance, RetainUntilDate = retainUntil },
        });

        var (code, message) = await Err(() => s3.DeleteObjectAsync(new DeleteObjectRequest { BucketName = name, Key = key, VersionId = versionId }));
        Ensure(code == "AccessDenied", $"expected AccessDenied deleting a retained version, got {Py.Repr(code)}");
        Ensure(message == "Access Denied because object protected by object lock.", $"unexpected message {Py.Repr(message)}");

        // effect absent: the refused delete must not have touched the retained version
        using (var get = await s3.GetObjectAsync(new GetObjectRequest { BucketName = name, Key = key, VersionId = versionId }))
        {
            var survivor = await S3Fidelity.ReadBody(get);
            Ensure(survivor.AsSpan().SequenceEqual("locked"u8), $"the refused delete altered the retained version, got {Py.Repr(survivor)}");
        }

        // with bypass, deletion must succeed (requires s3:BypassGovernanceRetention, granted to the test principal)
        await s3.DeleteObjectAsync(new DeleteObjectRequest { BucketName = name, Key = key, VersionId = versionId, BypassGovernanceRetention = true });

        // terminal read at the destination, through a different operation than the one that mutated
        var (gone, goneMessage) = await Err(() => s3.GetObjectAsync(new GetObjectRequest { BucketName = name, Key = key, VersionId = versionId }));
        Ensure(gone == "NoSuchVersion", $"expected NoSuchVersion after the bypass delete, got {Py.Repr(gone)}");
        Ensure(goneMessage == "The specified version does not exist.", $"unexpected message {Py.Repr(goneMessage)}");
    }

    /// <summary>The (Error.Code, Error.Message) of fn(); RAISES if fn succeeds.</summary>
    private static Task<(string? Code, string? Message)> Err(Func<Task> fn) =>
        S3Fidelity.ClientError(fn, "the call was expected to fail and it succeeded");
}
