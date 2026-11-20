// Translated from behavioral/s3/check_s3_object_lock_compliance_cannot_be_bypassed_while_governance_can.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Net;
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.s3-object-lock-compliance-cannot-be-bypassed-while-governance-can</summary>
[Category("AwsEvaluationSuite")]
public sealed class S3ObjectLockComplianceCannotBeBypassedWhileGovernanceCan
{
    private static readonly byte[] PayloadCompliance = "compliance-locked-bytes-no-principal-may-delete-early"u8.ToArray();
    private static readonly byte[] PayloadGovernance = "governance-locked-bytes-a-bypass-may-remove"u8.ToArray();
    private const int RetainS = 75;

    /// <summary>
    /// The same BypassGovernanceRetention DeleteObject is refused AccessDenied for a COMPLIANCE version
    /// (bytes intact) and succeeds for a GOVERNANCE version (gone) in the same bucket.
    ///
    /// Reference: https://docs.aws.amazon.com/AmazonS3/latest/userguide/object-lock.html
    /// </summary>
    [Test]
    [DisplayName("s3.s3-object-lock-compliance-cannot-be-bypassed-while-governance-can")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;

        // ObjectLockEnabledForBucket implies versioning, which object lock requires.
        var bucket = await S3Fidelity.CreateObjectLockBucket(ctx, "sim2real-behav-bkt-");

        Ensure(
            (await s3.GetBucketVersioningAsync(new GetBucketVersioningRequest { BucketName = bucket })).VersioningConfig?.Status == VersionStatus.Enabled,
            "ObjectLockEnabledForBucket did not implicitly enable versioning");
        var lockConf = (await s3.GetObjectLockConfigurationAsync(new GetObjectLockConfigurationRequest { BucketName = bucket })).ObjectLockConfiguration;
        Ensure(
            lockConf.ObjectLockEnabled == "Enabled",
            $"the bucket's object lock configuration is {{'ObjectLockEnabled': {Py.Repr(lockConf.ObjectLockEnabled?.Value)}}}, want ObjectLockEnabled=Enabled");

        var now = FidelityTime.UtcNow.UtcDateTime;
        var complianceUntil = now + TimeSpan.FromSeconds(RetainS);
        var governanceUntil = now + TimeSpan.FromMinutes(30);

        var complianceVersion = (await s3.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = "compliance.bin", InputStream = new MemoryStream(PayloadCompliance) })).VersionId;
        await s3.PutObjectRetentionAsync(new PutObjectRetentionRequest
        {
            BucketName = bucket,
            Key = "compliance.bin",
            VersionId = complianceVersion,
            Retention = new ObjectLockRetention { Mode = ObjectLockRetentionMode.Compliance, RetainUntilDate = complianceUntil },
        });
        var governanceVersion = (await s3.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = "governance.bin", InputStream = new MemoryStream(PayloadGovernance) })).VersionId;
        await s3.PutObjectRetentionAsync(new PutObjectRetentionRequest
        {
            BucketName = bucket,
            Key = "governance.bin",
            VersionId = governanceVersion,
            Retention = new ObjectLockRetention { Mode = ObjectLockRetentionMode.Governance, RetainUntilDate = governanceUntil },
        });

        var complianceRetention = (await s3.GetObjectRetentionAsync(new GetObjectRetentionRequest { BucketName = bucket, Key = "compliance.bin", VersionId = complianceVersion })).Retention;
        Ensure(complianceRetention.Mode == "COMPLIANCE", $"the compliance version's retention reads {Py.Repr(complianceRetention.Mode?.Value)}");
        var governanceRetention = (await s3.GetObjectRetentionAsync(new GetObjectRetentionRequest { BucketName = bucket, Key = "governance.bin", VersionId = governanceVersion })).Retention;
        Ensure(governanceRetention.Mode == "GOVERNANCE", $"the governance version's retention reads {Py.Repr(governanceRetention.Mode?.Value)}");

        var refused = await FidelityContext.CaptureError(() => s3.DeleteObjectAsync(new DeleteObjectRequest
        {
            BucketName = bucket,
            Key = "compliance.bin",
            VersionId = complianceVersion,
            BypassGovernanceRetention = true,
        }));
        Ensure(
            refused is not null,
            "DeleteObject with BypassGovernanceRetention=True removed a COMPLIANCE-retained version. COMPLIANCE is unbypassable by every principal including the account root until its RetainUntilDate — an emulator that honours the bypass flag here voids the entire write-once compliance guarantee its users are relying on");
        Ensure(refused.Code == "AccessDenied", $"expected AccessDenied for a bypass delete of a COMPLIANCE version, got {Py.Repr(refused.Code)}");
        Ensure(
            refused.Message == "Access Denied because object protected by object lock.",
            $"the COMPLIANCE refusal's message is {Py.Repr(refused.Message)}, want 'Access Denied because object protected by object lock.'");
        Ensure(refused.StatusCode == HttpStatusCode.Forbidden, $"expected HTTP 403, got {(int)refused.StatusCode}");

        using (var intact = await s3.GetObjectAsync(new GetObjectRequest { BucketName = bucket, Key = "compliance.bin", VersionId = complianceVersion }))
        {
            var bytes = await S3Fidelity.ReadBody(intact);
            Ensure(
                bytes.AsSpan().SequenceEqual(PayloadCompliance),
                $"the refused delete damaged the compliance object: it holds {Py.Repr(bytes)}, want {Py.Repr(PayloadCompliance)}");
        }

        Ensure(
            (await s3.GetObjectRetentionAsync(new GetObjectRetentionRequest { BucketName = bucket, Key = "compliance.bin", VersionId = complianceVersion })).Retention.Mode == "COMPLIANCE",
            "the refused delete altered the compliance version's retention mode");

        var bypassed = await s3.DeleteObjectAsync(new DeleteObjectRequest
        {
            BucketName = bucket,
            Key = "governance.bin",
            VersionId = governanceVersion,
            BypassGovernanceRetention = true,
        });
        Ensure(
            bypassed.HttpStatusCode == HttpStatusCode.NoContent,
            $"the governance bypass delete returned {(int)bypassed.HttpStatusCode}, expected 204 — GOVERNANCE retention IS bypassable and refusing it makes the two modes indistinguishable");
        Ensure(
            bypassed.VersionId == governanceVersion,
            $"the bypass delete reported VersionId {Py.Repr(bypassed.VersionId)}, want the targeted {Py.Repr(governanceVersion)}");

        var gone = await FidelityContext.CaptureError(() => s3.GetObjectAsync(new GetObjectRequest { BucketName = bucket, Key = "governance.bin", VersionId = governanceVersion }));
        Ensure(gone is not null, "the governance bypass delete reported success but the version is still readable");
        Ensure(gone.StatusCode == HttpStatusCode.NotFound, $"GetObject on the bypassed governance version returned {(int)gone.StatusCode}, expected 404");

        var surviving = ((await s3.ListVersionsAsync(new ListVersionsRequest { BucketName = bucket })).Versions ?? [])
            .Where(v => v.IsDeleteMarker != true).Select(v => (v.Key, v.VersionId)).ToList();
        Ensure(
            surviving.SequenceEqual([("compliance.bin", complianceVersion)]),
            $"the bucket's surviving versions are [{string.Join(", ", surviving.Select(s => $"({Py.Repr(s.Key)}, {Py.Repr(s.VersionId)})"))}]; want exactly the COMPLIANCE one, [('compliance.bin', {Py.Repr(complianceVersion)})] — the two retention modes did not diverge");
    }
}
