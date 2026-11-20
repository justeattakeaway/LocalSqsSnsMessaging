// Translated from behavioral/s3/check_s3crr_a_version_delete_is_never_replicated_even_with_marker_replication_enabled.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.s3crr-a-version-delete-is-never-replicated-even-with-marker-replication-enabled</summary>
[Category("AwsEvaluationSuite")]
public sealed class S3crrAVersionDeleteIsNeverReplicatedEvenWithMarkerReplicationEnabled
{
    private const string Check = "s3crr-a-version-delete-is-never-replicated-even-with-marker-replication-enabled";
    private const double ArrivalTimeoutSeconds = 240.0;
    private const double HoldFloorSeconds = 25.0;
    private const double HoldCapSeconds = 60.0;

    /// <summary>GetObject's refusal of a key with no current version, as real S3 returns it.</summary>
    private static readonly (string?, string?) NoSuchKey = ("NoSuchKey", "The specified key does not exist.");

    /// <summary>
    /// With DeleteMarkerReplication Enabled, a marker replicates but a delete by VersionId does not.
    /// The replica of the permanently deleted source version stays readable and gains no marker.
    /// </summary>
    [Test]
    [DisplayName("s3.s3crr-a-version-delete-is-never-replicated-even-with-marker-replication-enabled")]
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
        await S3Fidelity.EnableReplication(s3, source, roleArn,
            S3Fidelity.Rule("crr-with-marker-replication", 1, "", $"arn:{partition}:s3:::{dest}", deleteMarkers: true));

        const string versionKey = "crr/version-deleted.bin";
        const string markerKey = "crr/marker-deleted.bin";
        var versionBody = S3Fidelity.RepeatedBody(FidelityContext.UniqueName("versioned-"), 32);
        var markerBody = S3Fidelity.RepeatedBody(FidelityContext.UniqueName("markered-"), 32);
        var versionPut = await s3.PutObjectAsync(new PutObjectRequest { BucketName = source, Key = versionKey, InputStream = new MemoryStream(versionBody) });
        await s3.PutObjectAsync(new PutObjectRequest { BucketName = source, Key = markerKey, InputStream = new MemoryStream(markerBody) });

        foreach (var key in new[] { versionKey, markerKey })
        {
            await S3Fidelity.PollUntil(
                () => S3Fidelity.Try(() => peerS3.GetObjectMetadataAsync(dest, key)),
                code => code == S3Fidelity.Succeeded,
                TimeSpan.FromSeconds(ArrivalTimeoutSeconds),
                TimeSpan.FromSeconds(5.0),
                last => $"waited {ArrivalTimeoutSeconds:F0}s for the object {Py.Repr(key)} to replicate from {source} into {dest} in {peer} and it never happened; last observation: {Py.Repr(last)}");
        }

        var replica = await S3Fidelity.GetBytes(peerS3, dest, versionKey);
        Ensure(
            S3Fidelity.Sha256(replica) == S3Fidelity.Sha256(versionBody),
            $"the replica of {Py.Repr(versionKey)} is {replica.Length} bytes and does not match its " +
            $"{versionBody.Length}-byte source body; the survival claim below is about THIS replica");

        // --- Permanently remove that one version on the source. Not a marker: the version is gone.
        await s3.DeleteObjectAsync(new DeleteObjectRequest { BucketName = source, Key = versionKey, VersionId = versionPut.VersionId });
        var remaining = await S3Fidelity.Versions(s3, source, versionKey);
        Ensure(
            remaining.Count == 0,
            $"the source key {Py.Repr(versionKey)} still has versions " +
            $"[{string.Join(", ", remaining.Select(v => Py.Repr(v.VersionId)))}] after DeleteObject named VersionId " +
            $"{Py.Repr(versionPut.VersionId)}; a version delete removes the version outright");
        var sourceGone = await S3Fidelity.Error(() => s3.GetObjectAsync(source, versionKey), "the call was expected to fail and it succeeded");
        Ensure(
            (sourceGone.Code, sourceGone.Message) == NoSuchKey,
            $"{Py.Repr(versionKey)} is still readable in the SOURCE bucket after its only version was deleted");

        // --- Control on the same rule: a plain delete makes a marker, and THAT replicates.
        var marker = await s3.DeleteObjectAsync(new DeleteObjectRequest { BucketName = source, Key = markerKey });
        Ensure(
            marker.DeleteMarker == "true",
            $"deleting {Py.Repr(markerKey)} without a VersionId returned DeleteMarker " +
            $"{Py.Repr(marker.DeleteMarker)}; expected True");
        var started = FidelityTime.StartNew();
        await S3Fidelity.PollUntil(
            () => S3Fidelity.DeleteMarkers(peerS3, dest, markerKey),
            markers => markers.Count >= 1,
            TimeSpan.FromSeconds(ArrivalTimeoutSeconds),
            TimeSpan.FromSeconds(5.0),
            last => $"waited {ArrivalTimeoutSeconds:F0}s for the delete marker for {Py.Repr(markerKey)} to replicate into {dest} in {peer} under a rule " +
                $"whose DeleteMarkerReplication is Enabled and it never happened; last observation: {last.Count} marker(s)");
        var markerLatency = started.Elapsed.TotalSeconds;

        // The absence window is sized from the measured marker latency, clamped, not a fixed sleep.
        var hold = TimeSpan.FromSeconds(Math.Min(Math.Max(2.0 * markerLatency, HoldFloorSeconds), HoldCapSeconds));
        var hiddenReplica = await S3Fidelity.Error(() => peerS3.GetObjectAsync(dest, markerKey), "the call was expected to fail and it succeeded");
        Ensure(
            (hiddenReplica.Code, hiddenReplica.Message) == NoSuchKey,
            $"{Py.Repr(markerKey)} is still readable in {dest} although its delete marker replicated there");

        // --- The version-deleted object's replica stays exactly as it was, for a window this run
        // measured on the same wiring.
        var clock = FidelityTime.StartNew();
        while (clock.Elapsed < hold)
        {
            var held = await S3Fidelity.GetBytes(peerS3, dest, versionKey);
            Ensure(
                S3Fidelity.Sha256(held) == S3Fidelity.Sha256(versionBody),
                $"the replica of {Py.Repr(versionKey)} in {dest} ({peer}) is now {held.Length} bytes; a version " +
                "delete on the source is never replicated, so the replica must be untouched");
            var markers = await S3Fidelity.DeleteMarkers(peerS3, dest, versionKey);
            Ensure(
                markers.Count == 0,
                $"{dest} grew {markers.Count} delete marker(s) for {Py.Repr(versionKey)}, whose source version " +
                "was deleted by VersionId; only MARKER deletes replicate, and the other key's marker " +
                $"arrived in {markerLatency:F1}s on this same wiring");
            var surviving = await S3Fidelity.Versions(peerS3, dest, versionKey);
            Ensure(
                surviving.Count == 1,
                $"the replica of {Py.Repr(versionKey)} in {dest} now has {surviving.Count} version(s) " +
                $"([{string.Join(", ", surviving.Select(v => Py.Repr(v.VersionId)))}]); expected the single replicated version to " +
                "survive its source version's deletion");
            await ctx.Delay(TimeSpan.FromSeconds(5.0));
        }
    }
}
