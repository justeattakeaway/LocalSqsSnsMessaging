// Translated from behavioral/s3/check_s3crr_object_written_before_the_rule_is_never_backfilled.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.s3crr-object-written-before-the-rule-is-never-backfilled</summary>
[Category("AwsEvaluationSuite")]
public sealed class S3crrObjectWrittenBeforeTheRuleIsNeverBackfilled
{
    private const string Check = "s3crr-object-written-before-the-rule-is-never-backfilled";
    private const double ArrivalTimeoutSeconds = 240.0;
    private const double HoldFloorSeconds = 30.0;
    private const double HoldCapSeconds = 75.0;

    /// <summary>
    /// An object written before the replication rule is never copied; objects written after it are.
    /// S3 does not backfill existing objects (that takes S3 Batch Replication), and the pre-rule object
    /// carries no ReplicationStatus.
    /// </summary>
    [Test]
    [DisplayName("s3.s3crr-object-written-before-the-rule-is-never-backfilled")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var peer = S3Fidelity.PeerRegion(ctx);
        var peerS3 = ctx.S3Client(peer);
        var partition = ctx.CallerArn.Split(':')[1];
        var source = await S3Fidelity.NewVersionedBucket(ctx, s3, ctx.Region, "sim2real-behav-crr-src-", Check);
        var dest = await S3Fidelity.NewVersionedBucket(ctx, peerS3, peer, "sim2real-behav-crr-dst-", Check);

        // --- Written BEFORE any replication rule exists. Same bucket, same request shape as the
        // control objects below; the write order is the one thing that differs.
        const string legacyKey = "crr/legacy.bin";
        var legacyBody = S3Fidelity.RepeatedBody(FidelityContext.UniqueName("legacy-"), 48);
        await s3.PutObjectAsync(new PutObjectRequest { BucketName = source, Key = legacyKey, InputStream = new MemoryStream(legacyBody) });

        // The check creates the IAM role S3's replication engine assumes only to name it here; the cloud models no IAM.
        var roleArn = ctx.RoleArn(FidelityContext.UniqueName("sim2real-behav-crr-role-"));
        await S3Fidelity.EnableReplication(s3, source, roleArn, S3Fidelity.Rule("crr-everything", 1, "", $"arn:{partition}:s3:::{dest}"));

        // --- Control: an object written AFTER the rule crosses the boundary, and its latency sizes the
        // absence window below.
        const string firstKey = "crr/fresh-first.bin";
        var firstBody = S3Fidelity.RepeatedBody(FidelityContext.UniqueName("fresh-"), 48);
        await s3.PutObjectAsync(new PutObjectRequest { BucketName = source, Key = firstKey, InputStream = new MemoryStream(firstBody) });
        var started = FidelityTime.StartNew();
        await S3Fidelity.PollUntil(
            () => S3Fidelity.Try(() => peerS3.GetObjectMetadataAsync(dest, firstKey)),
            code => code == S3Fidelity.Succeeded,
            TimeSpan.FromSeconds(ArrivalTimeoutSeconds),
            TimeSpan.FromSeconds(5.0),
            last => $"waited {ArrivalTimeoutSeconds:F0}s for the control object {Py.Repr(firstKey)}, written after the rule, to reach {dest} in {peer} and it never happened; last observation: {Py.Repr(last)}");
        var latency = started.Elapsed.TotalSeconds;

        // The absence window is sized from the measured delivery latency, clamped, not a fixed sleep.
        var hold = TimeSpan.FromSeconds(Math.Min(Math.Max(2.0 * latency, HoldFloorSeconds), HoldCapSeconds));
        var arrived = await S3Fidelity.GetBytes(peerS3, dest, firstKey);
        Ensure(
            S3Fidelity.Sha256(arrived) == S3Fidelity.Sha256(firstBody),
            $"the control replica of {Py.Repr(firstKey)} is {arrived.Length} bytes and does not match the " +
            $"{firstBody.Length}-byte source body, so the pipeline this check measures against is not " +
            "copying bytes faithfully");

        // --- The pre-rule object is unchanged and carries no replication status on the source side.
        using (var legacySource = await s3.GetObjectAsync(source, legacyKey))
        {
            var legacyRead = await S3Fidelity.ReadBody(legacySource);
            Ensure(
                S3Fidelity.Sha256(legacyRead) == S3Fidelity.Sha256(legacyBody),
                $"the pre-rule object {Py.Repr(legacyKey)} in the SOURCE bucket is {legacyRead.Length} bytes and no " +
                $"longer matches what was written ({legacyBody.Length} bytes); this check is about it not " +
                "being COPIED, not about it being altered");
            Ensure(
                legacySource.ReplicationStatus is null,
                $"the pre-rule object {Py.Repr(legacyKey)} reports ReplicationStatus " +
                $"{Py.Repr(legacySource.ReplicationStatus?.Value)} on the source; S3 returns that header only for " +
                "objects that were eligible for replication when they were written, and this one predates " +
                "the rule entirely");
        }

        // --- Absence: it never appears in the peer region, held for a window derived from the control's
        // own measured latency and read on two observables.
        await S3Fidelity.HoldAbsent(
            peerS3,
            dest,
            legacyKey,
            "crr/legacy",
            hold,
            $"{Py.Repr(legacyKey)} was written to {source} BEFORE PutBucketReplication, so it is not " +
            $"replication material; the control object written after the rule reached {dest} in " +
            $"{latency:F1}s on this same wiring");

        // --- Re-prove delivery: the absence above was not a pipeline that stopped.
        const string secondKey = "crr/fresh-second.bin";
        var secondBody = S3Fidelity.RepeatedBody(FidelityContext.UniqueName("fresh2-"), 48);
        await s3.PutObjectAsync(new PutObjectRequest { BucketName = source, Key = secondKey, InputStream = new MemoryStream(secondBody) });
        await S3Fidelity.PollUntil(
            () => S3Fidelity.Try(() => peerS3.GetObjectMetadataAsync(dest, secondKey)),
            code => code == S3Fidelity.Succeeded,
            TimeSpan.FromSeconds(ArrivalTimeoutSeconds),
            TimeSpan.FromSeconds(5.0),
            last => $"waited {ArrivalTimeoutSeconds:F0}s for a second post-rule object {Py.Repr(secondKey)} to replicate, proving the pipeline stayed " +
                $"live throughout the {hold.TotalSeconds:F0}s absence window, and it never happened; last observation: {Py.Repr(last)}");
        var replayed = await S3Fidelity.GetBytes(peerS3, dest, secondKey);
        Ensure(
            S3Fidelity.Sha256(replayed) == S3Fidelity.Sha256(secondBody),
            $"the replica of {Py.Repr(secondKey)} is {replayed.Length} bytes and does not match its " +
            $"{secondBody.Length}-byte source body");
    }
}
