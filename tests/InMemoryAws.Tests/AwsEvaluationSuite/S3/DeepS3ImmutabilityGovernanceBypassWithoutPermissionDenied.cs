// Translated from behavioral/s3/check_deep_s3_immutability_governance_bypass_without_permission_denied.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Net;
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.deep-s3-immutability-governance-bypass-without-permission-denied</summary>
[Category("AwsEvaluationSuite")]
public sealed class DeepS3ImmutabilityGovernanceBypassWithoutPermissionDenied
{
    private const string Key = "governed.bin";
    private const double ConvergeS = 60.0;
    private static readonly byte[] Body = "governance-retained-bytes-only-a-permitted-bypass-may-remove"u8.ToArray();

    /// <summary>
    /// Under a Deny of s3:BypassGovernanceRetention, bypass delete and shorten are refused and change
    /// nothing while an extension succeeds; once the policy is removed the identical bypass delete
    /// succeeds, because the bypass header alone does not authorize lifting GOVERNANCE.
    /// </summary>
    [Test]
    [DisplayName("s3.deep-s3-immutability-governance-bypass-without-permission-denied")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var callerArn = ctx.CallerArn;
        var bucket = await S3Fidelity.CreateObjectLockBucket(ctx, "sim2real-imm-");

        var version = (await s3.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = Key, InputStream = new MemoryStream(Body) })).VersionId;
        var until = S3Fidelity.WholeSeconds(FidelityTime.UtcNow.UtcDateTime + TimeSpan.FromHours(1));
        await s3.PutObjectRetentionAsync(new PutObjectRetentionRequest
        {
            BucketName = bucket,
            Key = Key,
            VersionId = version,
            Retention = new ObjectLockRetention { Mode = ObjectLockRetentionMode.Governance, RetainUntilDate = until },
        });
        var ret = (await s3.GetObjectRetentionAsync(new GetObjectRetentionRequest { BucketName = bucket, Key = Key, VersionId = version })).Retention;
        Ensure(ret.Mode == "GOVERNANCE", $"retention did not land as GOVERNANCE: {Py.Repr(ret.Mode?.Value)}");

        var policy = $$"""
            {"Version": "2012-10-17", "Statement": [{"Sid": "DenyGovernanceBypass", "Effect": "Deny", "Principal": {"AWS": "{{callerArn}}"}, "Action": "s3:BypassGovernanceRetention", "Resource": "arn:aws:s3:::{{bucket}}/*"}]}
            """;
        await s3.PutBucketPolicyAsync(new PutBucketPolicyRequest { BucketName = bucket, Policy = policy });
        var bypassDenied =
            $"User: {callerArn} is not authorized to perform: s3:BypassGovernanceRetention on resource: \"arn:aws:s3:::{bucket}/{Key}\" with an explicit deny in a resource-based policy";

        // LIFO: this runs BEFORE the bucket teardown registered above, whose bypass deletes need it gone.
        ctx.OnCleanup(() => s3.DeleteBucketPolicyAsync(new DeleteBucketPolicyRequest { BucketName = bucket }));

        // --- The header without the permission: the delete is refused.
        var code = await Error(
            () => s3.DeleteObjectAsync(new DeleteObjectRequest { BucketName = bucket, Key = Key, VersionId = version, BypassGovernanceRetention = true }),
            "a bypass DeleteObject by a caller explicitly denied s3:BypassGovernanceRetention");
        Ensure(code == ("AccessDenied", bypassDenied), $"expected AccessDenied for a bypass delete without the bypass permission, got {code}");
        var body = await S3Fidelity.GetBytes(s3, bucket, Key);
        Ensure(body.AsSpan().SequenceEqual(Body), $"the refused bypass delete altered the version: it holds {Py.Repr(body)}");

        // --- Shortening a GOVERNANCE date is the other bypass-gated act, and is refused the same way.
        var shorter = until - TimeSpan.FromMinutes(30);
        code = await Error(
            () => s3.PutObjectRetentionAsync(new PutObjectRetentionRequest
            {
                BucketName = bucket,
                Key = Key,
                VersionId = version,
                BypassGovernanceRetention = true,
                Retention = new ObjectLockRetention { Mode = ObjectLockRetentionMode.Governance, RetainUntilDate = shorter },
            }),
            "a bypass shortening of GOVERNANCE retention without the bypass permission");
        Ensure(code == ("AccessDenied", bypassDenied), $"expected AccessDenied shortening GOVERNANCE retention without the permission, got {code}");
        var after = (await s3.GetObjectRetentionAsync(new GetObjectRetentionRequest { BucketName = bucket, Key = Key, VersionId = version })).Retention;
        Ensure(
            Math.Abs((S3Fidelity.Utc(after.RetainUntilDate) - until).TotalSeconds) < 1.0,
            $"the refused shorten moved RetainUntilDate to {after.RetainUntilDate:O}; want {until:O}");

        // --- Positive control under the SAME policy: extending needs no bypass, so it succeeds.
        var extended = until + TimeSpan.FromMinutes(30);
        await s3.PutObjectRetentionAsync(new PutObjectRetentionRequest
        {
            BucketName = bucket,
            Key = Key,
            VersionId = version,
            Retention = new ObjectLockRetention { Mode = ObjectLockRetentionMode.Governance, RetainUntilDate = extended },
        });
        after = (await s3.GetObjectRetentionAsync(new GetObjectRetentionRequest { BucketName = bucket, Key = Key, VersionId = version })).Retention;
        Ensure(
            after.Mode == "GOVERNANCE" && Math.Abs((S3Fidelity.Utc(after.RetainUntilDate) - extended).TotalSeconds) < 1.0,
            $"an extension under the bypass-deny policy read back as {Py.Repr(after.Mode?.Value)} until {after.RetainUntilDate:O}; want GOVERNANCE until {extended:O} — the Deny must gate the bypass, not every retention write");

        // --- Restore the permission. Policy removal propagates eventually, so poll for acceptance.
        await s3.DeleteBucketPolicyAsync(new DeleteBucketPolicyRequest { BucketName = bucket });
        var clock = FidelityTime.StartNew();
        DeleteObjectResponse deleted;
        while (true)
        {
            var (response, error) = await FidelityContext.Capture(() => s3.DeleteObjectAsync(new DeleteObjectRequest
            {
                BucketName = bucket,
                Key = Key,
                VersionId = version,
                BypassGovernanceRetention = true,
            }));
            if (response is not null)
            {
                deleted = response;
                break;
            }

            var last = (error!.Code, error.Message);
            Ensure(last == ("AccessDenied", bypassDenied), $"unexpected error during the permitted bypass: {last}");
            Ensure(clock.Elapsed.TotalSeconds < ConvergeS, $"a bypass delete was still refused {ConvergeS}s after the Deny policy was removed");
            await FidelityTime.Delay(TimeSpan.FromSeconds(2.0));
        }

        Ensure(deleted.HttpStatusCode == HttpStatusCode.NoContent, $"the permitted bypass delete returned {(int)deleted.HttpStatusCode}");
        Ensure(deleted.VersionId == version, $"the permitted bypass delete reported VersionId {Py.Repr(deleted.VersionId)}, want {Py.Repr(version)}");
        var gone = await Error(
            () => s3.GetObjectAsync(new GetObjectRequest { BucketName = bucket, Key = Key, VersionId = version }),
            "reading a version the permitted bypass just deleted");
        Ensure(gone == ("NoSuchVersion", "The specified version does not exist."), $"expected NoSuchVersion after the bypass delete, got {gone}");
    }

    /// <summary>Call fn(); return the S3 (error code, error message) it raised. A SUCCESS is itself the failure.</summary>
    private static async Task<(string?, string?)> Error(Func<Task> fn, string what)
    {
        var error = await FidelityContext.CaptureError(fn) ?? throw Fail($"{what} unexpectedly SUCCEEDED where real AWS refuses it");
        return (error.Code, error.Message);
    }
}
