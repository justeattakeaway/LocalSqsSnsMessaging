// Translated from behavioral/s3/check_s3crr_delete_marker_replicates_only_where_marker_replication_is_enabled.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.s3crr-delete-marker-replicates-only-where-marker-replication-is-enabled</summary>
[Category("AwsEvaluationSuite")]
public sealed class S3crrDeleteMarkerReplicatesOnlyWhereMarkerReplicationIsEnabled
{
    private const string Check = "s3crr-delete-marker-replicates-only-where-marker-replication-is-enabled";
    private const string MarkerPrefix = "mk/";
    private const string NoMarkerPrefix = "nomk/";
    private const double ArrivalTimeoutSeconds = 240.0;
    private const double HoldFloorSeconds = 25.0;
    private const double HoldCapSeconds = 60.0;

    /// <summary>GetObject's refusal of a key with no current version, as real S3 returns it.</summary>
    private static readonly (string?, string?) NoSuchKey = ("NoSuchKey", "The specified key does not exist.");

    /// <summary>
    /// A delete marker replicates only under the rule whose DeleteMarkerReplication is Enabled.
    /// The replicated marker hides the replica without destroying it; the other prefix's replica stays.
    /// </summary>
    [Test]
    [DisplayName("s3.s3crr-delete-marker-replicates-only-where-marker-replication-is-enabled")]
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
        var destArn = $"arn:{partition}:s3:::{dest}";
        await S3Fidelity.EnableReplication(
            s3,
            source,
            roleArn,
            S3Fidelity.Rule("markers-enabled", 1, MarkerPrefix, destArn, deleteMarkers: true),
            S3Fidelity.Rule("markers-disabled", 2, NoMarkerPrefix, destArn, deleteMarkers: false));

        var markedKey = $"{MarkerPrefix}object.bin";
        var keptKey = $"{NoMarkerPrefix}object.bin";
        var markedBody = S3Fidelity.RepeatedBody(FidelityContext.UniqueName("marked-"), 32);
        var keptBody = S3Fidelity.RepeatedBody(FidelityContext.UniqueName("kept-"), 32);
        await s3.PutObjectAsync(new PutObjectRequest { BucketName = source, Key = markedKey, InputStream = new MemoryStream(markedBody) });
        await s3.PutObjectAsync(new PutObjectRequest { BucketName = source, Key = keptKey, InputStream = new MemoryStream(keptBody) });

        // --- Both objects replicate first: the two prefixes are on identical wiring, so anything that
        // differs later is the DeleteMarkerReplication switch and nothing else.
        foreach (var key in new[] { markedKey, keptKey })
        {
            await S3Fidelity.PollUntil(
                () => S3Fidelity.Try(() => peerS3.GetObjectMetadataAsync(dest, key)),
                code => code == S3Fidelity.Succeeded,
                TimeSpan.FromSeconds(ArrivalTimeoutSeconds),
                TimeSpan.FromSeconds(5.0),
                last => $"waited {ArrivalTimeoutSeconds:F0}s for the object {Py.Repr(key)} to replicate from {source} into {dest} in {peer} and it never happened; last observation: {Py.Repr(last)}");
        }

        var keptReplica = await S3Fidelity.GetBytes(peerS3, dest, keptKey);
        Ensure(
            S3Fidelity.Sha256(keptReplica) == S3Fidelity.Sha256(keptBody),
            $"the replica of {Py.Repr(keptKey)} is {keptReplica.Length} bytes and does not match its " +
            $"{keptBody.Length}-byte source body; the rest of this check is about that replica surviving");

        // --- One DeleteObject per prefix, on the source.
        foreach (var key in new[] { markedKey, keptKey })
        {
            var marker = await s3.DeleteObjectAsync(new DeleteObjectRequest { BucketName = source, Key = key });
            Ensure(
                marker.DeleteMarker == "true",
                $"deleting {Py.Repr(key)} in the versioned source bucket returned DeleteMarker " +
                $"{Py.Repr(marker.DeleteMarker)}; expected True, so the source really is hidden");
        }

        foreach (var key in new[] { markedKey, keptKey })
        {
            var hidden = await S3Fidelity.Error(() => s3.GetObjectAsync(source, key), "the call was expected to fail and it succeeded");
            Ensure((hidden.Code, hidden.Message) == NoSuchKey, $"{Py.Repr(key)} is still readable in the SOURCE bucket after its delete marker was created");
        }

        // --- The enabled prefix's marker arrives. This is the positive control for delete replication.
        var started = FidelityTime.StartNew();
        await S3Fidelity.PollUntil(
            () => S3Fidelity.DeleteMarkers(peerS3, dest, markedKey),
            markers => markers.Count >= 1,
            TimeSpan.FromSeconds(ArrivalTimeoutSeconds),
            TimeSpan.FromSeconds(5.0),
            last => $"waited {ArrivalTimeoutSeconds:F0}s for the delete marker for {Py.Repr(markedKey)} to replicate into {dest} in {peer} under the " +
                $"rule whose DeleteMarkerReplication is Enabled and it never happened; last observation: {last.Count} marker(s)");
        var markerLatency = started.Elapsed.TotalSeconds;

        // The absence window is sized from the measured marker latency, clamped, not a fixed sleep.
        var hold = TimeSpan.FromSeconds(Math.Min(Math.Max(2.0 * markerLatency, HoldFloorSeconds), HoldCapSeconds));
        var hiddenReplica = await S3Fidelity.Error(() => peerS3.GetObjectAsync(dest, markedKey), "the call was expected to fail and it succeeded");
        Ensure(
            (hiddenReplica.Code, hiddenReplica.Message) == NoSuchKey,
            $"{Py.Repr(markedKey)} is still readable in {dest} although its delete marker replicated there; a " +
            "marker that does not hide the current version is not a delete marker");
        var surviving = await S3Fidelity.Versions(peerS3, dest, markedKey);
        Ensure(
            surviving.Count == 1,
            $"the replica of {Py.Repr(markedKey)} in {dest} has {surviving.Count} non-marker version(s) " +
            $"([{string.Join(", ", surviving.Select(v => Py.Repr(v.VersionId)))}]); a replicated delete marker must HIDE the " +
            "replica, not destroy it");

        // --- The disabled prefix's marker is withheld, for a window bounded by the enabled prefix's own
        // measured marker latency, while the same replica keeps returning the same bytes.
        var clock = FidelityTime.StartNew();
        while (clock.Elapsed < hold)
        {
            var held = await S3Fidelity.GetBytes(peerS3, dest, keptKey);
            Ensure(
                S3Fidelity.Sha256(held) == S3Fidelity.Sha256(keptBody),
                $"the replica of {Py.Repr(keptKey)} in {dest} changed to {held.Length} bytes while its source's " +
                "delete marker was supposed to be withheld");
            var withheld = await S3Fidelity.DeleteMarkers(peerS3, dest, keptKey);
            Ensure(
                withheld.Count == 0,
                $"{dest} grew {withheld.Count} delete marker(s) for {Py.Repr(keptKey)}, whose rule sets " +
                "DeleteMarkerReplication Status=Disabled; the OTHER prefix's marker replicated in " +
                $"{markerLatency:F1}s on this same wiring, so a propagated marker would be visible");
            await ctx.Delay(TimeSpan.FromSeconds(5.0));
        }
    }
}
