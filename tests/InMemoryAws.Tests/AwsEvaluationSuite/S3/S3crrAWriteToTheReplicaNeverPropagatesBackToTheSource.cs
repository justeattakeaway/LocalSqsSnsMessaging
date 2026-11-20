// Translated from behavioral/s3/check_s3crr_a_write_to_the_replica_never_propagates_back_to_the_source.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.s3crr-a-write-to-the-replica-never-propagates-back-to-the-source</summary>
[Category("AwsEvaluationSuite")]
public sealed class S3crrAWriteToTheReplicaNeverPropagatesBackToTheSource
{
    private const string Check = "s3crr-a-write-to-the-replica-never-propagates-back-to-the-source";
    private const double ArrivalTimeoutSeconds = 240.0;
    private const double HoldFloorSeconds = 30.0;
    private const double HoldCapSeconds = 75.0;

    /// <summary>
    /// Replication is one-way: overwriting the replica leaves the source bytes and versions as-is.
    /// Forward writes replicate before and after the window, so the absence is not a stalled pipeline.
    /// </summary>
    [Test]
    [DisplayName("s3.s3crr-a-write-to-the-replica-never-propagates-back-to-the-source")]
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

        // The check creates the IAM role S3's replication engine assumes only to name it here; the cloud models no IAM.
        var roleArn = ctx.RoleArn(FidelityContext.UniqueName("sim2real-behav-crr-role-"));
        await S3Fidelity.EnableReplication(s3, source, roleArn, S3Fidelity.Rule("crr-everything", 1, "", $"arn:{partition}:s3:::{dest}"));

        const string key = "crr/two-way.bin";
        var original = S3Fidelity.RepeatedBody(FidelityContext.UniqueName("origin-"), 32);
        var put = await s3.PutObjectAsync(new PutObjectRequest { BucketName = source, Key = key, InputStream = new MemoryStream(original) });
        var sourceVersion = put.VersionId;
        var started = FidelityTime.StartNew();
        await S3Fidelity.PollUntil(
            () => S3Fidelity.Try(() => peerS3.GetObjectMetadataAsync(dest, key)),
            code => code == S3Fidelity.Succeeded,
            TimeSpan.FromSeconds(ArrivalTimeoutSeconds),
            TimeSpan.FromSeconds(5.0),
            last => $"waited {ArrivalTimeoutSeconds:F0}s for the forward replica of {Py.Repr(key)} to appear in {dest} in {peer} and it never happened; last observation: {Py.Repr(last)}");
        var latency = started.Elapsed.TotalSeconds;

        // The absence window is sized from the measured delivery latency, clamped, not a fixed sleep.
        var hold = TimeSpan.FromSeconds(Math.Min(Math.Max(2.0 * latency, HoldFloorSeconds), HoldCapSeconds));
        var landed = await S3Fidelity.GetBytes(peerS3, dest, key);
        Ensure(
            S3Fidelity.Sha256(landed) == S3Fidelity.Sha256(original),
            $"the forward replica of {Py.Repr(key)} is {landed.Length} bytes and does not match the " +
            $"{original.Length}-byte source body; the reverse-direction claim below needs a faithful " +
            "forward replica to overwrite");

        // --- Write the replica directly, in the peer region. This call SUCCEEDS; the claim is about
        // what it does not cause.
        var overwritten = Enumerable.Repeat(System.Text.Encoding.UTF8.GetBytes(FidelityContext.UniqueName("replica-side-write-") + new string('!', 97)), 11).SelectMany(b => b).ToArray();
        var destPut = await peerS3.PutObjectAsync(new PutObjectRequest { BucketName = dest, Key = key, InputStream = new MemoryStream(overwritten) });
        using (var local = await peerS3.GetObjectAsync(dest, key))
        {
            var localBody = await S3Fidelity.ReadBody(local);
            Ensure(
                S3Fidelity.Sha256(localBody) == S3Fidelity.Sha256(overwritten),
                $"the write against the destination bucket returned VersionId {Py.Repr(destPut.VersionId)} but " +
                $"reading {Py.Repr(key)} back in {dest} gives {localBody.Length} bytes, not the " +
                $"{overwritten.Length} written; the reverse-direction claim needs this write to have landed");
            Ensure(
                local.ReplicationStatus is null,
                $"the object written DIRECTLY into {dest} reports ReplicationStatus " +
                $"{Py.Repr(local.ReplicationStatus?.Value)}; {dest} has no replication configuration, so a client " +
                "write there is an ordinary object and not a replica of anything");
        }

        // --- The source keeps its own bytes and its own single version for the whole window.
        var clock = FidelityTime.StartNew();
        while (clock.Elapsed < hold)
        {
            var held = await S3Fidelity.GetBytes(s3, source, key);
            Ensure(
                S3Fidelity.Sha256(held) == S3Fidelity.Sha256(original),
                $"the SOURCE object {Py.Repr(key)} in {source} ({ctx.Region}) changed to {held.Length} bytes " +
                $"after the replica was overwritten in {peer}; replication is one-way and the source " +
                "must be untouched");
            var versions = await S3Fidelity.Versions(s3, source, key);
            Ensure(
                versions.Select(v => v.VersionId).SequenceEqual([sourceVersion]),
                $"the source key {Py.Repr(key)} now has versions " +
                $"[{string.Join(", ", versions.Select(v => Py.Repr(v.VersionId)))}]; it must still hold exactly the one version " +
                $"{Py.Repr(sourceVersion)} this check wrote — a write to the replica reached back");
            await ctx.Delay(TimeSpan.FromSeconds(5.0));
        }

        // --- Re-prove the forward direction: the absence above was not a dead pipeline.
        const string forwardKey = "crr/forward-after.bin";
        var forwardBody = S3Fidelity.RepeatedBody(FidelityContext.UniqueName("forward-"), 32);
        await s3.PutObjectAsync(new PutObjectRequest { BucketName = source, Key = forwardKey, InputStream = new MemoryStream(forwardBody) });
        await S3Fidelity.PollUntil(
            () => S3Fidelity.Try(() => peerS3.GetObjectMetadataAsync(dest, forwardKey)),
            code => code == S3Fidelity.Succeeded,
            TimeSpan.FromSeconds(ArrivalTimeoutSeconds),
            TimeSpan.FromSeconds(5.0),
            last => $"waited {ArrivalTimeoutSeconds:F0}s for a further forward write {Py.Repr(forwardKey)} to replicate into {dest}, proving the pipeline " +
                $"stayed live throughout the {hold.TotalSeconds:F0}s window in which nothing travelled backwards, and it never happened; last observation: {Py.Repr(last)}");
        var forwardReplica = await S3Fidelity.GetBytes(peerS3, dest, forwardKey);
        Ensure(
            S3Fidelity.Sha256(forwardReplica) == S3Fidelity.Sha256(forwardBody),
            $"the replica of {Py.Repr(forwardKey)} is {forwardReplica.Length} bytes and does not match its " +
            $"{forwardBody.Length}-byte source body");
    }
}
