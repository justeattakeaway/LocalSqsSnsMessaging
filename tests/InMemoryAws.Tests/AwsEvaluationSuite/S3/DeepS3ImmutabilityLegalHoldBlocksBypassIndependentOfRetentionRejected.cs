// Translated from behavioral/s3/check_deep_s3_immutability_legal_hold_blocks_bypass_independent_of_retention_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Net;
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.deep-s3-immutability-legal-hold-blocks-bypass-independent-of-retention-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class DeepS3ImmutabilityLegalHoldBlocksBypassIndependentOfRetentionRejected
{
    private static readonly byte[] HeldBody = "retained-AND-held-bytes"u8.ToArray();
    private static readonly byte[] ControlBody = "retained-only-bytes"u8.ToArray();
    private static readonly (string, string) Locked = ("AccessDenied", "Access Denied because object protected by object lock.");

    /// <summary>
    /// Legal hold and retention are independent locks: a bypass delete that frees a retention-only
    /// version is refused on a held one, clearing retention leaves the hold in force, and only lifting
    /// the hold lets the version be deleted.
    /// </summary>
    [Test]
    [DisplayName("s3.deep-s3-immutability-legal-hold-blocks-bypass-independent-of-retention-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var bucket = await S3Fidelity.CreateObjectLockBucket(ctx, "sim2real-imm-");
        var until = FidelityTime.UtcNow.UtcDateTime + TimeSpan.FromHours(1);

        var held = (await s3.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = "held.bin", InputStream = new MemoryStream(HeldBody) })).VersionId;
        await s3.PutObjectRetentionAsync(new PutObjectRetentionRequest
        {
            BucketName = bucket,
            Key = "held.bin",
            VersionId = held,
            Retention = new ObjectLockRetention { Mode = ObjectLockRetentionMode.Governance, RetainUntilDate = until },
        });
        await s3.PutObjectLegalHoldAsync(new PutObjectLegalHoldRequest
        {
            BucketName = bucket,
            Key = "held.bin",
            VersionId = held,
            LegalHold = new ObjectLockLegalHold { Status = ObjectLockLegalHoldStatus.On },
        });
        var control = (await s3.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = "control.bin", InputStream = new MemoryStream(ControlBody) })).VersionId;
        await s3.PutObjectRetentionAsync(new PutObjectRetentionRequest
        {
            BucketName = bucket,
            Key = "control.bin",
            VersionId = control,
            Retention = new ObjectLockRetention { Mode = ObjectLockRetentionMode.Governance, RetainUntilDate = until },
        });

        var hold = (await s3.GetObjectLegalHoldAsync(new GetObjectLegalHoldRequest { BucketName = bucket, Key = "held.bin", VersionId = held })).LegalHold;
        Ensure(hold.Status == "ON", $"legal hold did not land: {{'Status': {Py.Repr(hold.Status?.Value)}}}");

        // --- The bypass that lifts retention does NOT lift a legal hold.
        var code = await Error(
            () => s3.DeleteObjectAsync(new DeleteObjectRequest { BucketName = bucket, Key = "held.bin", VersionId = held, BypassGovernanceRetention = true }),
            "a bypass DeleteObject of a GOVERNANCE-retained version that is also under legal hold");
        Ensure(code == Locked, $"expected AccessDenied for a bypass delete of a held version, got {code}");
        var body = await VersionBytes(s3, bucket, "held.bin", held);
        Ensure(body.AsSpan().SequenceEqual(HeldBody), $"the refused delete altered the held version: {Py.Repr(body)}");

        // --- Positive control: the byte-identical bypass delete on the retention-only version works.
        var ok = await s3.DeleteObjectAsync(new DeleteObjectRequest { BucketName = bucket, Key = "control.bin", VersionId = control, BypassGovernanceRetention = true });
        Ensure(ok.HttpStatusCode == HttpStatusCode.NoContent, $"the retention-only bypass delete returned {(int)ok.HttpStatusCode}");
        var gone = await Error(
            () => s3.GetObjectAsync(new GetObjectRequest { BucketName = bucket, Key = "control.bin", VersionId = control }),
            "reading the bypass-deleted control version");
        Ensure(gone == ("NoSuchVersion", "The specified version does not exist."), $"expected the control version gone (NoSuchVersion), got {gone}");

        // --- Remove the retention ENTIRELY: the hold alone still blocks deletion.
        await s3.PutObjectRetentionAsync(new PutObjectRetentionRequest
        {
            BucketName = bucket,
            Key = "held.bin",
            VersionId = held,
            BypassGovernanceRetention = true,
            Retention = new ObjectLockRetention(),
        });
        var noRet = await Error(
            () => s3.GetObjectRetentionAsync(new GetObjectRetentionRequest { BucketName = bucket, Key = "held.bin", VersionId = held }),
            "reading a retention that was just cleared");
        Ensure(
            noRet == ("NoSuchObjectLockConfiguration", "The specified object does not have a ObjectLock configuration"),
            $"expected NoSuchObjectLockConfiguration after clearing retention, got {noRet}");
        hold = (await s3.GetObjectLegalHoldAsync(new GetObjectLegalHoldRequest { BucketName = bucket, Key = "held.bin", VersionId = held })).LegalHold;
        Ensure(hold.Status == "ON", $"clearing the retention changed the legal hold to {{'Status': {Py.Repr(hold.Status?.Value)}}} — the two must be independent");
        code = await Error(
            () => s3.DeleteObjectAsync(new DeleteObjectRequest { BucketName = bucket, Key = "held.bin", VersionId = held }),
            "a DeleteObject of a version under legal hold with no retention at all");
        Ensure(code == Locked, $"expected AccessDenied deleting a held version with no retention, got {code}");
        body = await VersionBytes(s3, bucket, "held.bin", held);
        Ensure(body.AsSpan().SequenceEqual(HeldBody), $"the hold-only refusal altered the version: {Py.Repr(body)}");

        // --- Lift the hold: now, and only now, the plain delete frees the version.
        await s3.PutObjectLegalHoldAsync(new PutObjectLegalHoldRequest
        {
            BucketName = bucket,
            Key = "held.bin",
            VersionId = held,
            LegalHold = new ObjectLockLegalHold { Status = ObjectLockLegalHoldStatus.Off },
        });
        var freed = await s3.DeleteObjectAsync(new DeleteObjectRequest { BucketName = bucket, Key = "held.bin", VersionId = held });
        Ensure(freed.VersionId == held, $"the delete after lifting the hold reported VersionId {Py.Repr(freed.VersionId)}, want {Py.Repr(held)}");
        gone = await Error(
            () => s3.GetObjectAsync(new GetObjectRequest { BucketName = bucket, Key = "held.bin", VersionId = held }),
            "reading the version deleted after its hold was lifted");
        Ensure(gone == ("NoSuchVersion", "The specified version does not exist."), $"expected NoSuchVersion after the freed delete, got {gone}");
    }

    private static async Task<byte[]> VersionBytes(IAmazonS3 s3, string bucket, string key, string versionId)
    {
        using var get = await s3.GetObjectAsync(new GetObjectRequest { BucketName = bucket, Key = key, VersionId = versionId });
        return await S3Fidelity.ReadBody(get);
    }

    /// <summary>Call fn(); return the S3 (error code, error message) it raised. A SUCCESS is itself the failure.</summary>
    private static async Task<(string?, string?)> Error(Func<Task> fn, string what)
    {
        var error = await FidelityContext.CaptureError(fn) ?? throw Fail($"{what} unexpectedly SUCCEEDED where real AWS refuses it");
        return (error.Code, error.Message);
    }
}
