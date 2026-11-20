// Translated from behavioral/s3/check_object_legal_hold_blocks_delete.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.object-legal-hold-blocks-delete</summary>
[Category("AwsEvaluationSuite")]
public sealed class ObjectLegalHoldBlocksDelete
{
    private const string LockedMessage = "Access Denied because object protected by object lock.";
    private const string NoSuchVersionMessage = "The specified version does not exist.";

    /// <summary>
    /// DeleteObject on a version under legal hold fails with AccessDenied and its exact message, even
    /// for the owner, and the version survives; after the hold is released the same delete succeeds and
    /// the version is NoSuchVersion.
    /// </summary>
    [Test]
    [DisplayName("s3.object-legal-hold-blocks-delete")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var name = await S3Fidelity.CreateObjectLockBucket(ctx);
        const string key = "held.txt";
        var put = await s3.PutObjectAsync(new PutObjectRequest { BucketName = name, Key = key, ContentBody = "held" });
        var versionId = put.VersionId;

        await s3.PutObjectLegalHoldAsync(new PutObjectLegalHoldRequest
        {
            BucketName = name,
            Key = key,
            VersionId = versionId,
            LegalHold = new ObjectLockLegalHold { Status = ObjectLockLegalHoldStatus.On },
        });

        var (code, message) = await Err(() => s3.DeleteObjectAsync(new DeleteObjectRequest { BucketName = name, Key = key, VersionId = versionId }));
        Ensure(code == "AccessDenied", $"expected AccessDenied deleting a legal-hold version, got {Py.Repr(code)}");
        Ensure(message == LockedMessage, $"unexpected AccessDenied message: {Py.Repr(message)}");

        // the forbidden effect did not happen: the version is still there, readable
        using (var get = await s3.GetObjectAsync(new GetObjectRequest { BucketName = name, Key = key, VersionId = versionId }))
        {
            var body = await S3Fidelity.ReadBody(get);
            Ensure(body.AsSpan().SequenceEqual("held"u8), $"expected version to survive the blocked delete, got body {Py.Repr(body)}");
        }

        // release the hold, then the identical delete call succeeds, and an independent read
        // (a different operation than the one that mutated) proves the version is really gone
        await s3.PutObjectLegalHoldAsync(new PutObjectLegalHoldRequest
        {
            BucketName = name,
            Key = key,
            VersionId = versionId,
            LegalHold = new ObjectLockLegalHold { Status = ObjectLockLegalHoldStatus.Off },
        });
        await s3.DeleteObjectAsync(new DeleteObjectRequest { BucketName = name, Key = key, VersionId = versionId });
        var (notFound, notFoundMessage) = await Err(() => s3.GetObjectAsync(new GetObjectRequest { BucketName = name, Key = key, VersionId = versionId }));
        Ensure(notFound == "NoSuchVersion", $"expected NoSuchVersion after the real delete, got {Py.Repr(notFound)}");
        Ensure(notFoundMessage == NoSuchVersionMessage, $"unexpected NoSuchVersion message: {Py.Repr(notFoundMessage)}");
    }

    /// <summary>
    /// Call fn(); return the (code, message) of the ClientError it raises. An unexpected success raises,
    /// so a target that accepts what AWS refuses cannot read as a code mismatch.
    /// </summary>
    private static Task<(string? Code, string? Message)> Err(Func<Task> fn) =>
        S3Fidelity.ClientError(fn, "expected a ClientError, but the call SUCCEEDED");
}
