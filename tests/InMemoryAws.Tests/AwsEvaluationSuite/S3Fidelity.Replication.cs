using System.Security.Cryptography;
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite;

/// <summary>What the S3 replication ports share.</summary>
public static partial class S3Fidelity
{
    /// <summary>
    /// A replication rule as the checks write one: <c>{"ID", "Status": "Enabled", "Priority",
    /// "Filter": {"Prefix"}, "DeleteMarkerReplication": {"Status"}, "Destination": {"Bucket"}}</c>.
    /// </summary>
    public static ReplicationRule Rule(string id, int priority, string prefix, string destinationArn, bool deleteMarkers = false) => new()
    {
        Id = id,
        Status = ReplicationRuleStatus.Enabled,
        Priority = priority,
        Filter = new ReplicationRuleFilter { Prefix = prefix },
        DeleteMarkerReplication = new DeleteMarkerReplication
        {
            Status = deleteMarkers ? DeleteMarkerReplicationStatus.Enabled : DeleteMarkerReplicationStatus.Disabled,
        },
        Destination = new ReplicationDestination { BucketArn = destinationArn },
    };

    /// <summary><c>s3.put_bucket_replication(Bucket=source, ReplicationConfiguration={"Role": role, "Rules": rules})</c>.</summary>
    public static Task PutReplication(IAmazonS3 s3, string source, string role, params ReplicationRule[] rules)
    {
        ArgumentNullException.ThrowIfNull(s3);
        return s3.PutBucketReplicationAsync(new PutBucketReplicationRequest
        {
            BucketName = source,
            Configuration = new ReplicationConfiguration { Role = role, Rules = [.. rules] },
        });
    }

    /// <summary>
    /// <c>_new_versioned_bucket(ctx, client, region, prefix)</c> of the cross-region checks: a uniquely
    /// named bucket in <paramref name="region"/>, tagged with the check, with versioning enabled (and
    /// read back as such), emptied and deleted at cleanup.
    /// </summary>
    public static async Task<string> NewVersionedBucket(FidelityContext ctx, IAmazonS3 client, string region, string prefix, string check)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentNullException.ThrowIfNull(client);
        var name = await CreateBucket(ctx, prefix, client as AmazonS3Client, region: region);
        await client.PutBucketTaggingAsync(new PutBucketTaggingRequest
        {
            BucketName = name,
            TagSet = [new Tag { Key = "sim2real:managed", Value = "true" }, new Tag { Key = "sim2real:check", Value = check }],
        });
        await PollUntil(
            () => Try(() => client.PutBucketVersioningAsync(new PutBucketVersioningRequest
            {
                BucketName = name,
                VersioningConfig = new S3BucketVersioningConfig { Status = VersionStatus.Enabled },
            })),
            code => code == Succeeded,
            TimeSpan.FromSeconds(60.0),
            TimeSpan.FromSeconds(3.0),
            last => $"waited 60s for PutBucketVersioning to be accepted on the new bucket {name} in {region} and it never happened; last observation: {Py.Repr(last)}");
        var status = (await client.GetBucketVersioningAsync(new GetBucketVersioningRequest { BucketName = name })).VersioningConfig.Status;
        Ensure(
            status == VersionStatus.Enabled,
            $"bucket {name} in {region} reports versioning {Py.Repr(status?.Value)} after PutBucketVersioning; " +
            "replication requires 'Enabled' on both ends");
        return name;
    }

    /// <summary>
    /// <c>_enable_replication(ctx, s3, source, config)</c>: PutBucketReplication (retried while a new role
    /// propagates), then the rules read back by (ID, Filter.Prefix, DeleteMarkerReplication.Status).
    /// </summary>
    public static async Task EnableReplication(IAmazonS3 s3, string source, string role, params ReplicationRule[] rules)
    {
        ArgumentNullException.ThrowIfNull(s3);
        await PollUntil(
            () => Try(() => PutReplication(s3, source, role, rules)),
            code => code == Succeeded,
            TimeSpan.FromSeconds(120.0),
            TimeSpan.FromSeconds(5.0),
            last => $"waited 120s for PutBucketReplication to accept the freshly created replication role and it never happened; last observation: {Py.Repr(last)}");
        var stored = (await s3.GetBucketReplicationAsync(new GetBucketReplicationRequest { BucketName = source })).Configuration;
        static string Describe(IEnumerable<(string? Id, string? Prefix, string? Status)> rules) =>
            "[" + string.Join(", ", rules.Order().Select(r => $"({Py.Repr(r.Id)}, {Py.Repr(r.Prefix)}, {Py.Repr(r.Status)})")) + "]";
        var want = Describe(rules.Select(r => ((string?)r.Id, (string?)r.Filter?.Prefix, (string?)r.DeleteMarkerReplication?.Status?.Value)));
        var got = Describe(stored.Rules.Select(r => ((string?)r.Id, (string?)r.Filter?.Prefix, (string?)r.DeleteMarkerReplication?.Status?.Value)));
        Ensure(
            got == want,
            $"GetBucketReplication on {source} reports rules {got}; expected {want} — the " +
            "configuration the rest of this check reasons about is not the one in effect");
    }

    /// <summary>Every non-marker version of exactly <paramref name="key"/>.</summary>
    public static async Task<List<S3ObjectVersion>> Versions(IAmazonS3 s3, string bucket, string key)
    {
        ArgumentNullException.ThrowIfNull(s3);
        var listing = await s3.ListVersionsAsync(new ListVersionsRequest { BucketName = bucket, Prefix = key });
        return [.. (listing.Versions ?? []).Where(v => v.Key == key && v.IsDeleteMarker != true)];
    }

    /// <summary>Every delete marker on exactly <paramref name="key"/>.</summary>
    public static async Task<List<S3ObjectVersion>> DeleteMarkers(IAmazonS3 s3, string bucket, string key)
    {
        ArgumentNullException.ThrowIfNull(s3);
        var listing = await s3.ListVersionsAsync(new ListVersionsRequest { BucketName = bucket, Prefix = key });
        return [.. (listing.Versions ?? []).Where(v => v.Key == key && v.IsDeleteMarker == true)];
    }

    /// <summary>
    /// <c>_hold_absent(client, bucket, key, prefix, hold_s, reason=...)</c>: asserts <paramref name="key"/>
    /// stays absent from <paramref name="bucket"/> for <paramref name="hold"/>, on two observables: a 404
    /// HeadObject and an empty listing of <paramref name="prefix"/>.
    /// </summary>
    public static async Task HoldAbsent(IAmazonS3 s3, string bucket, string key, string prefix, TimeSpan hold, string reason)
    {
        ArgumentNullException.ThrowIfNull(s3);
        var clock = FidelityTime.StartNew();
        while (clock.Elapsed < hold)
        {
            var error = await FidelityContext.CaptureError(() => s3.GetObjectMetadataAsync(bucket, key));
            if (error is null)
            {
                throw Fail($"{Py.Repr(key)} appeared in {bucket}: {reason}");
            }

            Ensure(
                error.StatusCode == System.Net.HttpStatusCode.NotFound,
                $"HeadObject on {Py.Repr(key)} in {bucket} failed with HTTP {(int)error.StatusCode} rather than 404 " +
                $"while proving absence: {reason}");
            var found = ((await s3.ListObjectsV2Async(new ListObjectsV2Request { BucketName = bucket, Prefix = prefix })).S3Objects ?? [])
                .Select(o => o.Key)
                .ToList();
            Ensure(found.Count == 0, $"{bucket} lists [{string.Join(", ", found.Select(Py.Repr))}] under prefix {Py.Repr(prefix)}: {reason}");
            await FidelityTime.Delay(TimeSpan.FromSeconds(5.0));
        }
    }

    /// <summary><c>hashlib.sha256(data).hexdigest()</c>.</summary>
    public static string Sha256(byte[] data) => Convert.ToHexStringLower(SHA256.HashData(data));

    /// <summary>The checks' bodies: <c>(prefix.encode() + b"-" + bytes(range(256))) * count</c>.</summary>
    public static byte[] RepeatedBody(string prefix, int count)
    {
        var unit = System.Text.Encoding.UTF8.GetBytes(prefix).Append((byte)'-').Concat(Enumerable.Range(0, 256).Select(i => (byte)i)).ToArray();
        return [.. Enumerable.Repeat(unit, count).SelectMany(b => b)];
    }
}
