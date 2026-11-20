// Translated from behavioral/s3/check_deep_s3_immutability_compliance_lock_extends_and_overwrites_but_weakening_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Globalization;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.deep-s3-immutability-compliance-lock-extends-and-overwrites-but-weakening-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class DeepS3ImmutabilityComplianceLockExtendsAndOverwritesButWeakeningRejected
{
    private static readonly byte[] V1 = "compliance-locked-original"u8.ToArray();
    private static readonly byte[] V2 = "overwrite-as-a-new-version"u8.ToArray();

    // COMPLIANCE retention cannot be undone by any principal until it lapses, so it is kept short
    // and the check body waits the lapse out itself, keeping teardown within its bounded budget.
    private const int RetainS = 12;
    private const int ExtendS = 8;
    private const double LapseBudgetS = 150.0;
    private const double LapsePollS = 1.0;

    private static readonly (string, string) Locked = ("AccessDenied", "Access Denied because object protected by object lock.");

    /// <summary>
    /// A COMPLIANCE lock is per-version and ratchet-only: it can be extended and the key overwritten or
    /// delete-marked, but downgrade and clear are AccessDenied even with bypass, and the version stays
    /// undeletable until the extended date by the server's clock.
    /// </summary>
    [Test]
    [DisplayName("s3.deep-s3-immutability-compliance-lock-extends-and-overwrites-but-weakening-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var bucket = await S3Fidelity.CreateObjectLockBucket(ctx, "sim2real-imm-");
        const string key = "ledger.bin";

        var v1 = (await s3.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = key, InputStream = new MemoryStream(V1) })).VersionId;
        var until = S3Fidelity.WholeSeconds(FidelityTime.UtcNow.UtcDateTime + TimeSpan.FromSeconds(RetainS));
        await s3.PutObjectRetentionAsync(new PutObjectRetentionRequest
        {
            BucketName = bucket,
            Key = key,
            VersionId = v1,
            Retention = new ObjectLockRetention { Mode = ObjectLockRetentionMode.Compliance, RetainUntilDate = until },
        });

        // --- Positive: an extension of a COMPLIANCE date is permitted and reads back.
        var extended = until + TimeSpan.FromSeconds(ExtendS);
        await s3.PutObjectRetentionAsync(new PutObjectRetentionRequest
        {
            BucketName = bucket,
            Key = key,
            VersionId = v1,
            Retention = new ObjectLockRetention { Mode = ObjectLockRetentionMode.Compliance, RetainUntilDate = extended },
        });
        var ret = (await s3.GetObjectRetentionAsync(new GetObjectRetentionRequest { BucketName = bucket, Key = key, VersionId = v1 })).Retention;
        Ensure(ret.Mode == "COMPLIANCE", $"the extension changed the mode: {Describe(ret)}");
        Ensure(
            Math.Abs((S3Fidelity.Utc(ret.RetainUntilDate) - extended).TotalSeconds) < 1.0,
            $"the extension read back RetainUntilDate {ret.RetainUntilDate:O}; want {extended:O}");

        // --- Negative 1: downgrading COMPLIANCE to GOVERNANCE, even with the bypass header.
        var code = await Error(
            () => s3.PutObjectRetentionAsync(new PutObjectRetentionRequest
            {
                BucketName = bucket,
                Key = key,
                VersionId = v1,
                BypassGovernanceRetention = true,
                Retention = new ObjectLockRetention { Mode = ObjectLockRetentionMode.Governance, RetainUntilDate = extended },
            }),
            "downgrading a COMPLIANCE retention to GOVERNANCE");
        Ensure(code == Locked, $"expected AccessDenied downgrading COMPLIANCE, got {code}");

        // --- Negative 2: clearing the COMPLIANCE retention, even with the bypass header.
        code = await Error(
            () => s3.PutObjectRetentionAsync(new PutObjectRetentionRequest
            {
                BucketName = bucket,
                Key = key,
                VersionId = v1,
                BypassGovernanceRetention = true,
                Retention = new ObjectLockRetention(),
            }),
            "clearing a COMPLIANCE retention");
        Ensure(code == Locked, $"expected AccessDenied clearing COMPLIANCE, got {code}");

        // Effect absent: neither refusal weakened the lock.
        ret = (await s3.GetObjectRetentionAsync(new GetObjectRetentionRequest { BucketName = bucket, Key = key, VersionId = v1 })).Retention;
        Ensure(
            ret.Mode == "COMPLIANCE" && Math.Abs((S3Fidelity.Utc(ret.RetainUntilDate) - extended).TotalSeconds) < 1.0,
            $"a refused weakening changed the retention to {Describe(ret)}");

        // --- Positive: the KEY is still writable — the PUT lands as a NEW, unlocked version.
        var put2 = await s3.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = key, InputStream = new MemoryStream(V2) });
        var v2 = put2.VersionId;
        Ensure(v2 != v1, $"the overwrite reused the locked VersionId {Py.Repr(v1)}");
        var head = await s3.GetObjectMetadataAsync(bucket, key);
        Ensure(head.VersionId == v2, $"the current version is {Py.Repr(head.VersionId)}; the overwrite {Py.Repr(v2)} should be current");
        Ensure(
            head.ObjectLockMode is null,
            $"the new version inherited ObjectLockMode {Py.Repr(head.ObjectLockMode?.Value)}; locks are per-version and this bucket has no default retention");
        var current = await S3Fidelity.GetBytes(s3, bucket, key);
        Ensure(current.AsSpan().SequenceEqual(V2), $"a versionless GET returned {Py.Repr(current)}, want the overwrite {Py.Repr(V2)}");

        // --- Positive: a simple DELETE of the key is allowed — it writes a delete marker.
        var marker = await s3.DeleteObjectAsync(new DeleteObjectRequest { BucketName = bucket, Key = key });
        Ensure(
            marker.DeleteMarker == "true",
            $"a versionless DELETE on a key with a locked version returned DeleteMarker={Py.Repr(marker.DeleteMarker)}; want a delete marker");
        var hidden = await Error(() => s3.GetObjectAsync(bucket, key), "a versionless GET behind a delete marker");
        Ensure(hidden == ("NoSuchKey", "The specified key does not exist."), $"expected NoSuchKey behind the delete marker, got {hidden}");

        // --- The locked version is untouched by all of it, and still refuses a version delete.
        using (var locked = await s3.GetObjectAsync(new GetObjectRequest { BucketName = bucket, Key = key, VersionId = v1 }))
        {
            Ensure((await S3Fidelity.ReadBody(locked)).AsSpan().SequenceEqual(V1), "the locked version's bytes changed");
            Ensure(locked.ObjectLockMode == "COMPLIANCE", $"GetObject on the locked version reported ObjectLockMode {Py.Repr(locked.ObjectLockMode?.Value)}");
        }

        code = await Error(
            () => s3.DeleteObjectAsync(new DeleteObjectRequest { BucketName = bucket, Key = key, VersionId = v1, BypassGovernanceRetention = true }),
            "a bypass version-delete of a COMPLIANCE version");
        Ensure(code == Locked, $"expected AccessDenied deleting the COMPLIANCE version, got {code}");

        var versions = ((await s3.ListVersionsAsync(new ListVersionsRequest { BucketName = bucket, Prefix = key })).Versions ?? [])
            .Where(v => v.IsDeleteMarker != true && v.Key == key).Select(v => v.VersionId).Order(StringComparer.Ordinal).ToList();
        Ensure(
            versions.SequenceEqual(new[] { v1, v2 }.Order(StringComparer.Ordinal)),
            $"the key's versions are {Py.Repr(versions)}; want exactly the locked {Py.Repr(v1)} and the overwrite {Py.Repr(v2)}");

        // Poll until the lock lapses; checked against the server Date so an emulator enforcing the
        // original date fails. The bound asserts that the lapse arrives, not how fast.
        string? serverDate = null;
        void OnResponse(object sender, ResponseEventArgs e)
        {
            if (e is WebServiceResponseEventArgs response && response.ResponseHeaders.TryGetValue("Date", out var date))
            {
                serverDate = date;
            }
        }

        var lapseBudgetS = BudgetS(ctx, LapseBudgetS);
        var clock = FidelityTime.StartNew();
        s3.AfterResponseEvent += OnResponse;
        try
        {
            while (true)
            {
                serverDate = null;
                var error = await FidelityContext.CaptureError(() => s3.DeleteObjectAsync(new DeleteObjectRequest { BucketName = bucket, Key = key, VersionId = v1 }));
                if (error is null)
                {
                    break;
                }

                var live = (error.Code, error.Message);
                Ensure(live == Locked, $"unexpected error while the lock was live: {live}");
                if (clock.Elapsed.TotalSeconds >= lapseBudgetS && BudgetSpent(ctx))
                {
                    throw new TimeoutException("the COMPLIANCE lock to lapse: the run budget ended first; not graded");
                }

                Ensure(
                    clock.Elapsed.TotalSeconds < lapseBudgetS,
                    $"the COMPLIANCE version was still undeletable {lapseBudgetS:F0}s after the run began waiting; its RetainUntilDate was {extended:O}");
                await FidelityTime.Delay(TimeSpan.FromSeconds(LapsePollS));
            }
        }
        finally
        {
            s3.AfterResponseEvent -= OnResponse;
        }

        var serverNow = DateTimeOffset.Parse(serverDate ?? throw Fail("the delete's response carried no Date header"), CultureInfo.InvariantCulture).UtcDateTime;
        Ensure(
            serverNow >= extended - TimeSpan.FromSeconds(1),
            $"the COMPLIANCE version was deleted at server time {serverNow:O}, BEFORE its extended RetainUntilDate {extended:O} — the extension was accepted but not enforced");
        var gone = await Error(
            () => s3.GetObjectAsync(new GetObjectRequest { BucketName = bucket, Key = key, VersionId = v1 }),
            "reading the COMPLIANCE version after its lapse-delete");
        Ensure(gone == ("NoSuchVersion", "The specified version does not exist."), $"expected NoSuchVersion after the lapse-delete, got {gone}");
    }

    /// <summary>Call fn(); return the S3 (error code, error message) it raised. A SUCCESS is itself the failure.</summary>
    private static async Task<(string?, string?)> Error(Func<Task> fn, string what)
    {
        var error = await FidelityContext.CaptureError(fn) ?? throw Fail($"{what} unexpectedly SUCCEEDED where real AWS refuses it");
        return (error.Code, error.Message);
    }

    /// <summary>A poll's timeout: capS, bounded by the runner's remaining time less 5 s so the poll fails with its own message.</summary>
    private static double BudgetS(FidelityContext ctx, double capS) => Math.Max(1.0, Math.Min(capS, ctx.TimeLeftSeconds - 5.0));

    /// <summary>True when the runner's remaining time, not the wait's own deadline, ended a capped wait.</summary>
    private static bool BudgetSpent(FidelityContext ctx) => ctx.TimeLeftSeconds <= 6.0;

    private static string Describe(ObjectLockRetention retention) =>
        $"{{'Mode': {Py.Repr(retention.Mode?.Value)}, 'RetainUntilDate': {retention.RetainUntilDate:O}}}";
}
