// Translated from behavioral/s3/check_replication_covers_the_eligible_prefix_only_and_withholds_the_delete_marker.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Text;
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.replication-covers-the-eligible-prefix-only-and-withholds-the-delete-marker</summary>
[Category("AwsEvaluationSuite")]
public sealed class ReplicationCoversTheEligiblePrefixOnlyAndWithholdsTheDeleteMarker
{
    private const string Check = "replication-covers-the-eligible-prefix-only-and-withholds-the-delete-marker";
    private const string Eligible = "repl/";
    private const string Ineligible = "keep/";
    private const double LandTimeoutSeconds = 420.0;
    private const double HoldCapSeconds = 240.0;

    /// <summary>
    /// Replication delivers only what a rule selects, and a disabled-marker rule withholds deletes.
    /// Checks ReplicationStatus COMPLETED/REPLICA, no status on the unmatched object, and a later
    /// eligible object still replicating.
    /// Reference: https://docs.aws.amazon.com/AmazonS3/latest/userguide/replication.html
    /// </summary>
    [Test]
    [DisplayName("s3.replication-covers-the-eligible-prefix-only-and-withholds-the-delete-marker")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var source = await NewVersionedBucket(ctx);
        var dest = await NewVersionedBucket(ctx);

        // The check creates an IAM role S3 can assume only to name it here; the cloud models no IAM.
        var roleArn = ctx.RoleArn(FidelityContext.UniqueName("sim2real-behav-repl-role-"));
        var rule = S3Fidelity.Rule("eligible-prefix-only", 1, Eligible, $"arn:aws:s3:::{dest}");

        // The replication role's trust policy takes a few seconds to become visible to S3's replication
        // engine, so PutBucketReplication can transiently refuse a brand-new role.
        await S3Fidelity.PollUntil(
            () => S3Fidelity.Try(() => S3Fidelity.PutReplication(s3, source, roleArn, rule)),
            code => code == S3Fidelity.Succeeded,
            TimeSpan.FromSeconds(120.0),
            TimeSpan.FromSeconds(5.0),
            last => $"waited 120s for PutBucketReplication to accept the freshly created replication role and it never happened; last observation: {Py.Repr(last)}");
        var rules = (await s3.GetBucketReplicationAsync(new GetBucketReplicationRequest { BucketName = source })).Configuration.Rules;
        Ensure(
            rules.Select(r => r.Filter?.Prefix).SequenceEqual([Eligible]),
            $"GetBucketReplication reports filters [{string.Join(", ", rules.Select(r => Py.Repr(r.Filter?.Prefix)))}]; expected exactly " +
            $"one rule filtered on prefix {Py.Repr(Eligible)}");

        var eligibleKey = $"{Eligible}first.bin";
        var ineligibleKey = $"{Ineligible}first.bin";
        var eligibleBody = "eligible-payload-" + new string('E', 512);
        var ineligibleBody = "ineligible-payload-" + new string('I', 512);
        await s3.PutObjectAsync(new PutObjectRequest { BucketName = source, Key = eligibleKey, ContentBody = eligibleBody });
        await s3.PutObjectAsync(new PutObjectRequest { BucketName = source, Key = ineligibleKey, ContentBody = ineligibleBody });

        // --- Control: the eligible object lands in the destination, and L is its measured latency.
        var started = FidelityTime.StartNew();
        await S3Fidelity.PollUntil(
            () => S3Fidelity.Try(() => s3.GetObjectMetadataAsync(dest, eligibleKey)),
            code => code == S3Fidelity.Succeeded,
            TimeSpan.FromSeconds(LandTimeoutSeconds),
            TimeSpan.FromSeconds(5.0),
            last => $"waited {LandTimeoutSeconds:F0}s for the eligible object {Py.Repr(eligibleKey)} to be replicated into {dest} and it never happened; last observation: {Py.Repr(last)}");
        var latency = started.Elapsed.TotalSeconds;

        // Absences are held for 3x the measured replication latency (min 15s, capped), not a sleep.
        var hold = TimeSpan.FromSeconds(Math.Min(Math.Max(3.0 * latency, 15.0), HoldCapSeconds));
        var replica = Encoding.UTF8.GetString(await S3Fidelity.GetBytes(s3, dest, eligibleKey));
        Ensure(
            replica == eligibleBody,
            $"the replica of {Py.Repr(eligibleKey)} is {replica.Length} bytes and does not match the " +
            $"{eligibleBody.Length}-byte source body");

        // --- The replication-status header, on both sides. Nothing is pinned: the values come from the
        // documented vocabulary and the source's own convergence.
        var sourceStatus = await S3Fidelity.PollUntil(
            async () => (await s3.GetObjectMetadataAsync(source, eligibleKey)).ReplicationStatus?.Value,
            status => status == "COMPLETED",
            TimeSpan.FromSeconds(Math.Max(120.0, 2.0 * latency)),
            TimeSpan.FromSeconds(5.0),
            last => $"the source object {Py.Repr(eligibleKey)} to report ReplicationStatus COMPLETED never happened; last observation: {Py.Repr(last)}");
        Ensure(sourceStatus == "COMPLETED", $"the source object reports ReplicationStatus {Py.Repr(sourceStatus)}, expected 'COMPLETED'");
        var destHead = await s3.GetObjectMetadataAsync(dest, eligibleKey);
        Ensure(
            destHead.ReplicationStatus?.Value == "REPLICA",
            "the destination object reports ReplicationStatus " +
            $"{Py.Repr(destHead.ReplicationStatus?.Value)}, expected 'REPLICA' — a replica that does not " +
            "identify itself as one cannot be told apart from a client-side copy");

        // --- Absence 1: the ineligible object was never eligible, so it carries no status at all and
        // never appears at the destination. Held for hold_s, with a second observable.
        var sourceIneligible = await s3.GetObjectMetadataAsync(source, ineligibleKey);
        Ensure(
            sourceIneligible.ReplicationStatus is null,
            $"the source object {Py.Repr(ineligibleKey)} — which matches no rule — reports ReplicationStatus " +
            $"{Py.Repr(sourceIneligible.ReplicationStatus?.Value)}; S3 returns that header only for objects " +
            "eligible for replication");
        await S3Fidelity.HoldAbsent(
            s3,
            dest,
            ineligibleKey,
            Ineligible,
            hold,
            $"{Py.Repr(ineligibleKey)} matches no replication rule, so it must never reach {dest}; the " +
            $"eligible object landed in {latency:F1}s on this same wiring");

        // --- Absence 2: a delete marker on the source, with DeleteMarkerReplication Disabled, is
        // withheld. The source really is hidden; the replica really is not.
        var marker = await s3.DeleteObjectAsync(new DeleteObjectRequest { BucketName = source, Key = eligibleKey });
        Ensure(
            marker.DeleteMarker == "true",
            $"deleting {Py.Repr(eligibleKey)} on a versioned bucket returned DeleteMarker " +
            $"{Py.Repr(marker.DeleteMarker)}; expected True");
        var hidden = await S3Fidelity.Error(() => s3.GetObjectAsync(source, eligibleKey));
        Ensure(
            (hidden.Code, hidden.Message) == ("NoSuchKey", "The specified key does not exist."),
            $"{Py.Repr(eligibleKey)} is still readable on the SOURCE after its delete marker was created: " +
            $"{S3Fidelity.Repr((hidden.Code, hidden.Message))}");
        var clock = FidelityTime.StartNew();
        while (clock.Elapsed < hold)
        {
            var held = Encoding.UTF8.GetString(await S3Fidelity.GetBytes(s3, dest, eligibleKey));
            Ensure(
                held == eligibleBody,
                $"the replica of {Py.Repr(eligibleKey)} changed to {held.Length} bytes while the source's " +
                "delete marker was being withheld");
            await ctx.Delay(TimeSpan.FromSeconds(5.0));
        }

        var destMarkers = (await s3.ListVersionsAsync(new ListVersionsRequest { BucketName = dest, Prefix = eligibleKey })).Versions?
            .Where(v => v.IsDeleteMarker == true).ToList() ?? [];
        Ensure(
            destMarkers.Count == 0,
            $"the destination grew {destMarkers.Count} delete marker(s) for " +
            $"{Py.Repr(eligibleKey)} within {hold.TotalSeconds:F0}s, but the rule sets DeleteMarkerReplication " +
            $"Status=Disabled; the eligible object itself replicated in {latency:F1}s, so a " +
            "propagated marker would have arrived by now");

        // --- Re-prove delivery: the pipeline is still alive, so neither absence above was "nothing is
        // replicating any more".
        var secondKey = $"{Eligible}second.bin";
        var secondBody = "second-eligible-payload-" + new string('S', 512);
        await s3.PutObjectAsync(new PutObjectRequest { BucketName = source, Key = secondKey, ContentBody = secondBody });
        await S3Fidelity.PollUntil(
            () => S3Fidelity.Try(() => s3.GetObjectMetadataAsync(dest, secondKey)),
            code => code == S3Fidelity.Succeeded,
            TimeSpan.FromSeconds(Math.Max(180.0, 4.0 * latency)),
            TimeSpan.FromSeconds(5.0),
            last => $"a second eligible object {Py.Repr(secondKey)} to replicate, proving the pipeline was live " +
                $"throughout both absence windows, never happened; last observation: {Py.Repr(last)}");
        Ensure(
            Encoding.UTF8.GetString(await S3Fidelity.GetBytes(s3, dest, secondKey)) == secondBody,
            $"the replica of {Py.Repr(secondKey)} does not match its source body");
    }

    /// <summary>Create a uniquely-named, tagged, versioning-enabled bucket with teardown registered.</summary>
    private static async Task<string> NewVersionedBucket(FidelityContext ctx)
    {
        var s3 = ctx.S3;
        var name = await S3Fidelity.CreateBucket(ctx, "sim2real-behav-bkt-");
        await s3.PutBucketTaggingAsync(new PutBucketTaggingRequest
        {
            BucketName = name,
            TagSet = [new Tag { Key = "sim2real:managed", Value = "true" }, new Tag { Key = "sim2real:check", Value = Check }],
        });
        await s3.PutBucketVersioningAsync(new PutBucketVersioningRequest
        {
            BucketName = name,
            VersioningConfig = new S3BucketVersioningConfig { Status = VersionStatus.Enabled },
        });
        return name;
    }
}
