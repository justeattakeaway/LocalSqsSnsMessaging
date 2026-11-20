// Translated from behavioral/s3/check_object_legal_hold_lifecycle.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.object-legal-hold-lifecycle</summary>
[Category("AwsEvaluationSuite")]
public sealed class ObjectLegalHoldLifecycle
{
    private const string LockedMessage = "Access Denied because object protected by object lock.";
    private const string NoSuchVersionMessage = "The specified version does not exist.";

    /// <summary>
    /// PutObjectLegalHold toggles ON/OFF and GetObjectLegalHold reflects the current hold status at
    /// each step (an object with an ON hold cannot be deleted until it is switched back OFF).
    /// </summary>
    [Test]
    [DisplayName("s3.object-legal-hold-lifecycle")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        const string key = "legal-hold-test.txt";
        var name = await S3Fidelity.CreateObjectLockBucket(ctx, "sim2real-behav-legalhold-");
        await s3.PutBucketTaggingAsync(new PutBucketTaggingRequest { BucketName = name, TagSet = [new Tag { Key = "project", Value = "sim2real" }] });

        await s3.PutObjectAsync(new PutObjectRequest { BucketName = name, Key = key, ContentBody = "legal-hold-payload" });
        var versions = (await s3.ListVersionsAsync(new ListVersionsRequest { BucketName = name, Prefix = key })).Versions.Where(v => v.IsDeleteMarker != true).ToList();
        var versionId = versions[0].VersionId;

        await s3.PutObjectLegalHoldAsync(new PutObjectLegalHoldRequest { BucketName = name, Key = key, LegalHold = new ObjectLockLegalHold { Status = ObjectLockLegalHoldStatus.On } });
        var got = await s3.GetObjectLegalHoldAsync(new GetObjectLegalHoldRequest { BucketName = name, Key = key });
        Ensure(got.LegalHold.Status == "ON", $"{{'LegalHold': {{'Status': {Py.Repr(got.LegalHold.Status?.Value)}}}}}");

        // While the hold is ON, deletion of that specific object *version* must be
        // blocked (a plain, version-less DeleteObject would merely add a delete
        // marker and is not blocked by a legal hold, so we must target the
        // version explicitly to observe the protection).
        var (code, message) = await Error(() => s3.DeleteObjectAsync(new DeleteObjectRequest
        {
            BucketName = name,
            Key = key,
            VersionId = versionId,
            BypassGovernanceRetention = true,
        }));
        Ensure(code == "AccessDenied", $"expected AccessDenied deleting a held version, got {Py.Repr(code)}");
        Ensure(message == LockedMessage, $"unexpected AccessDenied message: {Py.Repr(message)}");

        // the forbidden effect did not happen: the version is still there, readable
        using (var get = await s3.GetObjectAsync(new GetObjectRequest { BucketName = name, Key = key, VersionId = versionId }))
        {
            var body = await S3Fidelity.ReadBody(get);
            Ensure(body.AsSpan().SequenceEqual("legal-hold-payload"u8), $"expected version to survive the blocked delete, got body {Py.Repr(body)}");
        }

        await s3.PutObjectLegalHoldAsync(new PutObjectLegalHoldRequest { BucketName = name, Key = key, LegalHold = new ObjectLockLegalHold { Status = ObjectLockLegalHoldStatus.Off } });
        var got2 = await s3.GetObjectLegalHoldAsync(new GetObjectLegalHoldRequest { BucketName = name, Key = key });
        Ensure(got2.LegalHold.Status == "OFF", $"{{'LegalHold': {{'Status': {Py.Repr(got2.LegalHold.Status?.Value)}}}}}");

        // Now deletion of that specific version succeeds, and an independent read
        // (a different operation than the one that mutated) proves it is really gone.
        await s3.DeleteObjectAsync(new DeleteObjectRequest { BucketName = name, Key = key, VersionId = versionId });
        var (notFound, notFoundMessage) = await Error(() => s3.GetObjectAsync(new GetObjectRequest { BucketName = name, Key = key, VersionId = versionId }));
        Ensure(notFound == "NoSuchVersion", $"expected NoSuchVersion after the real delete, got {Py.Repr(notFound)}");
        Ensure(notFoundMessage == NoSuchVersionMessage, $"unexpected NoSuchVersion message: {Py.Repr(notFoundMessage)}");
    }

    /// <summary>
    /// Call fn(); return the (code, message) of the ClientError it raises. An unexpected success raises,
    /// so a target that accepts what AWS refuses cannot read as a code mismatch.
    /// </summary>
    private static Task<(string? Code, string? Message)> Error(Func<Task> fn) =>
        S3Fidelity.ClientError(fn, "expected a ClientError, but the call SUCCEEDED");
}
