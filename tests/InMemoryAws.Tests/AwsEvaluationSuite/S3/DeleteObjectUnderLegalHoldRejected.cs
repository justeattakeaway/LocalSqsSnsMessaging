// Translated from behavioral/s3/check_delete_object_under_legal_hold_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.delete-object-under-legal-hold-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class DeleteObjectUnderLegalHoldRejected
{
    /// <summary>
    /// DeleteObject of a version under an Object Lock legal hold is refused with AccessDenied and the
    /// version survives; once the hold is turned OFF the same delete removes it.
    /// </summary>
    [Test]
    [DisplayName("s3.delete-object-under-legal-hold-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        string bucket;
        try
        {
            bucket = await S3Fidelity.CreateObjectLockBucket(ctx, "sim2real-legalhold-");
        }
        catch (AmazonS3Exception exc)
        {
            throw Fail($"could not create Object-Lock-enabled bucket: {exc.Message}");
        }

        const string key = "held-object.txt";
        var versionId = (await s3.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = key, ContentBody = "protected-content" })).VersionId;

        await s3.PutObjectLegalHoldAsync(new PutObjectLegalHoldRequest
        {
            BucketName = bucket,
            Key = key,
            VersionId = versionId,
            LegalHold = new ObjectLockLegalHold { Status = ObjectLockLegalHoldStatus.On },
        });
        var status = (await s3.GetObjectLegalHoldAsync(new GetObjectLegalHoldRequest { BucketName = bucket, Key = key, VersionId = versionId })).LegalHold.Status;
        Ensure(status == "ON", "assert status == \"ON\"");

        var err = await Error(() => s3.DeleteObjectAsync(new DeleteObjectRequest { BucketName = bucket, Key = key, VersionId = versionId }));
        Ensure(
            err == ("AccessDenied", "Access Denied because object protected by object lock."),
            $"expected AccessDenied with AWS's exact message while legal hold is ON, got ({Py.Repr(err.Code)}, {Py.Repr(err.Message)})");

        // the forbidden effect did not happen: the version is still there, readable
        using (var get = await s3.GetObjectAsync(new GetObjectRequest { BucketName = bucket, Key = key, VersionId = versionId }))
        {
            Ensure((await S3Fidelity.ReadBody(get)).AsSpan().SequenceEqual("protected-content"u8), "assert body == b\"protected-content\"");
        }

        // release the hold, then the identical delete call succeeds and the version is gone
        await s3.PutObjectLegalHoldAsync(new PutObjectLegalHoldRequest
        {
            BucketName = bucket,
            Key = key,
            VersionId = versionId,
            LegalHold = new ObjectLockLegalHold { Status = ObjectLockLegalHoldStatus.Off },
        });
        await s3.DeleteObjectAsync(new DeleteObjectRequest { BucketName = bucket, Key = key, VersionId = versionId });
        var notFound = await Error(() => s3.GetObjectAsync(new GetObjectRequest { BucketName = bucket, Key = key, VersionId = versionId }));
        Ensure(
            notFound == ("NoSuchVersion", "The specified version does not exist."),
            $"expected NoSuchVersion after real delete, got ({Py.Repr(notFound.Code)}, {Py.Repr(notFound.Message)})");
    }

    /// <summary>Call fn(); return the ClientError (Code, Message). An unexpected success raises.</summary>
    private static Task<(string? Code, string? Message)> Error(Func<Task> fn) =>
        S3Fidelity.ClientError(fn, "expected a ClientError, but the call succeeded");
}
