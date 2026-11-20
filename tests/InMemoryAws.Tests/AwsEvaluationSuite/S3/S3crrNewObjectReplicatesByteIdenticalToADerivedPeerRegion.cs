// Translated from behavioral/s3/check_s3crr_new_object_replicates_byte_identical_to_a_derived_peer_region.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.s3crr-new-object-replicates-byte-identical-to-a-derived-peer-region</summary>
[Category("AwsEvaluationSuite")]
public sealed class S3crrNewObjectReplicatesByteIdenticalToADerivedPeerRegion
{
    private const string Check = "s3crr-new-object-replicates-byte-identical-to-a-derived-peer-region";
    private const double ArrivalTimeoutSeconds = 240.0;
    private const double StatusTimeoutSeconds = 120.0;

    /// <summary>
    /// A new object replicates to a derived peer region with identical bytes, ETag, type, metadata.
    /// The source converges to ReplicationStatus COMPLETED and the replica reports REPLICA.
    /// </summary>
    [Test]
    [DisplayName("s3.s3crr-new-object-replicates-byte-identical-to-a-derived-peer-region")]
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
        var constraint = (await peerS3.GetBucketLocationAsync(new GetBucketLocationRequest { BucketName = dest })).Location?.Value;
        var located = string.IsNullOrEmpty(constraint) ? "us-east-1" : constraint;
        Ensure(
            located == peer && located != ctx.Region,
            $"the destination bucket {Py.Repr(dest)} reports LocationConstraint {Py.Repr(located)}; this check is " +
            $"about CROSS-region replication and needs it in the derived peer region {Py.Repr(peer)}, not in " +
            $"the source region {Py.Repr(ctx.Region)}");

        // The check creates the IAM role S3's replication engine assumes only to name it here; the cloud models no IAM.
        var roleArn = ctx.RoleArn(FidelityContext.UniqueName("sim2real-behav-crr-role-"));
        await S3Fidelity.EnableReplication(s3, source, roleArn, S3Fidelity.Rule("crr-everything", 1, "", $"arn:{partition}:s3:::{dest}"));

        const string key = "crr/new-object.bin";
        var token = FidelityContext.UniqueName("payload-");
        var body = S3Fidelity.RepeatedBody(token, 96);
        var putRequest = new PutObjectRequest
        {
            BucketName = source,
            Key = key,
            InputStream = new MemoryStream(body),
            ContentType = "application/octet-stream",
        };
        putRequest.Metadata.Add("crr-origin", token);
        var put = await s3.PutObjectAsync(putRequest);
        var started = FidelityTime.StartNew();
        await S3Fidelity.PollUntil(
            () => S3Fidelity.Try(() => peerS3.GetObjectMetadataAsync(dest, key)),
            code => code == S3Fidelity.Succeeded,
            TimeSpan.FromSeconds(ArrivalTimeoutSeconds),
            TimeSpan.FromSeconds(5.0),
            last => $"waited {ArrivalTimeoutSeconds:F0}s for the object {Py.Repr(key)} written to {source} in {ctx.Region} to appear in {dest} in {peer} and it never happened; last observation: {Py.Repr(last)}");
        var latency = started.Elapsed.TotalSeconds;

        using var replica = await peerS3.GetObjectAsync(dest, key);
        var replicaBody = await S3Fidelity.ReadBody(replica);
        Ensure(
            S3Fidelity.Sha256(replicaBody) == S3Fidelity.Sha256(body),
            $"the replica of {Py.Repr(key)} in {dest} ({peer}) is {replicaBody.Length} bytes with sha256 " +
            $"{Py.Repr(S3Fidelity.Sha256(replicaBody))}; the source object is {body.Length} bytes with " +
            $"sha256 {Py.Repr(S3Fidelity.Sha256(body))} — replication must copy the bytes, not " +
            "something derived from them");
        Ensure(
            replica.ETag == put.ETag,
            $"the replica's ETag is {Py.Repr(replica.ETag)} and the source's is {Py.Repr(put.ETag)}; a replica " +
            "that re-mints its entity tag cannot be compared with its source by any client");
        Ensure(
            replica.Headers.ContentType == "application/octet-stream",
            $"the replica reports ContentType {Py.Repr(replica.Headers.ContentType)}, not the source's " +
            "'application/octet-stream'");
        Ensure(
            replica.Metadata["x-amz-meta-crr-origin"] == token,
            $"the replica's user metadata does not carry crr-origin; expected crr-origin={Py.Repr(token)} to " +
            "have been carried across the region boundary with the object");

        var sourceStatus = await S3Fidelity.PollUntil(
            async () => (await s3.GetObjectMetadataAsync(source, key)).ReplicationStatus?.Value,
            status => status == "COMPLETED",
            TimeSpan.FromSeconds(StatusTimeoutSeconds),
            TimeSpan.FromSeconds(5.0),
            last => $"waited {StatusTimeoutSeconds:F0}s for the source object {Py.Repr(key)} in {source} to report ReplicationStatus COMPLETED (its " +
                $"replica was already readable in {peer} after {latency:F1}s) and it never happened; last observation: {Py.Repr(last)}");
        Ensure(sourceStatus == "COMPLETED", $"the source object reports ReplicationStatus {Py.Repr(sourceStatus)}, expected 'COMPLETED'");
        Ensure(
            replica.ReplicationStatus?.Value == "REPLICA",
            $"the object in {dest} reports ReplicationStatus {Py.Repr(replica.ReplicationStatus?.Value)}, " +
            "expected 'REPLICA' — a replica that does not identify itself as one is " +
            "indistinguishable from a client-side copy");
    }
}
