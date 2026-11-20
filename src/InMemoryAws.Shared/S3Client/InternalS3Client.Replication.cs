using System.Globalization;
using System.Net;
using InMemoryAws.S3.Model;

namespace InMemoryAws.S3;

/// <summary>
/// Replication: a bucket's replication configuration, and copying each new version (and, where a
/// rule says so, each delete marker) a rule selects into the rule's destination bucket.
/// </summary>
/// <remarks>
/// Replication happens as the version is written, so a replica is readable as soon as the write
/// returns, and the source version reports <c>COMPLETED</c> (or <c>FAILED</c>, when the destination
/// is gone or no longer versioned) from the start. A replica keeps its source's version id, ETag,
/// metadata and tags, reports <c>REPLICA</c>, and is not replicated again. Objects written before
/// a rule are never copied (that is S3 Batch Replication's work), and deleting a version by id is
/// never replicated. The role is not assumed: the library models no IAM, so the role can always
/// read the source and write the destination.
/// </remarks>
internal sealed partial class InternalS3Client
{
    /// <summary>
    /// Sets the bucket's replication configuration, validated as S3 validates it: the bucket must be
    /// versioned, a rule with a <c>Filter</c> must say whether delete markers replicate, priorities must
    /// differ, a destination with <c>ReplicationTime</c> also names <c>Metrics</c>, and each
    /// destination bucket must exist and be versioned.
    /// </summary>
    public override Task<PutBucketReplicationOutput> PutBucketReplicationAsync(PutBucketReplicationRequest request, CancellationToken cancellationToken)
    {
        var configuration = request.ReplicationConfiguration;
        if (configuration?.Rules is not { Count: > 0 } rules || string.IsNullOrEmpty(configuration.Role))
        {
            throw S3Errors.MalformedXml();
        }

        lock (Store.Gate)
        {
            var bucket = GetBucket(request.Bucket);
            if (!bucket.VersioningEnabled)
            {
                throw S3Errors.InvalidRequest("Versioning must be 'Enabled' on the bucket to apply a replication configuration");
            }

            foreach (var rule in rules)
            {
                ValidateReplicationRule(rule);
            }

            var priorities = new HashSet<int>();
            foreach (var rule in rules)
            {
                if (rule.Priority is { } priority && !priorities.Add(priority))
                {
                    throw S3Errors.InvalidRequest($"Found duplicate priority {priority.ToString(CultureInfo.InvariantCulture)}.");
                }
            }

            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (var rule in rules)
            {
                if (rule.ID is { } id && !ids.Add(id))
                {
                    throw S3Errors.InvalidArgument("Rule Id must be unique.");
                }
            }

            foreach (var rule in rules)
            {
                var destination = DestinationBucket(rule) ?? throw S3Errors.InvalidRequest("Destination bucket must exist.");
                if (!destination.VersioningEnabled)
                {
                    throw S3Errors.InvalidRequest("Destination bucket must have versioning enabled.");
                }
            }

            bucket.Replication = configuration;
            return Task.FromResult(new PutBucketReplicationOutput());
        }
    }

    public override Task<GetBucketReplicationOutput> GetBucketReplicationAsync(GetBucketReplicationRequest request, CancellationToken cancellationToken)
    {
        lock (Store.Gate)
        {
            var bucket = GetBucket(request.Bucket);
            return Task.FromResult(new GetBucketReplicationOutput
            {
                ReplicationConfiguration = bucket.Replication
                    ?? throw new S3Exception(
                        "ReplicationConfigurationNotFoundError",
                        "The replication configuration was not found",
                        HttpStatusCode.NotFound,
                        ("BucketName", bucket.Name)),
            });
        }
    }

    public override Task<DeleteBucketReplicationOutput> DeleteBucketReplicationAsync(DeleteBucketReplicationRequest request, CancellationToken cancellationToken)
    {
        lock (Store.Gate)
        {
            GetBucket(request.Bucket).Replication = null;
            return Task.FromResult(new DeleteBucketReplicationOutput());
        }
    }

    /// <summary>
    /// Replicates a version just written to <paramref name="bucket"/>: copies it to the destination of
    /// each rule that selects it (the highest-priority rule per destination), and records the outcome
    /// on the version. A replica, or a version no rule selects, is left alone.
    /// </summary>
    private void ReplicateWrite(S3Bucket bucket, S3ObjectVersion version)
    {
        if (version.ReplicationStatus == "REPLICA")
        {
            return;
        }

        var selected = SelectingRules(bucket, version);
        if (selected.Count == 0)
        {
            return;
        }

        var completed = true;
        foreach (var rule in selected)
        {
            var destination = DestinationBucket(rule);
            if (destination is null || !destination.VersioningEnabled)
            {
                completed = false;
                continue;
            }

            var replica = version.With(version.Key, version.VersionId, version.LastModified);
            replica.ReplicationStatus = "REPLICA";
            if (rule.Destination?.StorageClass is { } storageClass)
            {
                replica = WithStorageClass(replica, storageClass);
            }

            destination.Add(replica);
        }

        version.ReplicationStatus = completed ? "COMPLETED" : "FAILED";
    }

    /// <summary>
    /// Replicates a delete marker just created in <paramref name="bucket"/>, to the destination of each
    /// rule that selects its key and replicates delete markers. Rules filtered on tags never do.
    /// </summary>
    private void ReplicateDeleteMarker(S3Bucket bucket, S3ObjectVersion marker)
    {
        foreach (var rule in SelectingRules(bucket, marker))
        {
            var replicatesMarkers = rule.Filter is null && rule.DeleteMarkerReplication is null
                ? true
                : rule.DeleteMarkerReplication?.Status == "Enabled";
            if (replicatesMarkers && !HasTagFilter(rule) && DestinationBucket(rule) is { VersioningEnabled: true } destination)
            {
                var replica = marker.With(marker.Key, marker.VersionId, marker.LastModified);
                replica.ReplicationStatus = "REPLICA";
                destination.Add(replica);
            }
        }
    }

    /// <summary>The enabled rules that select <paramref name="version"/>, the highest-priority one per destination.</summary>
    private static List<ReplicationRule> SelectingRules(S3Bucket bucket, S3ObjectVersion version)
    {
        if (bucket.Replication?.Rules is not { } rules)
        {
            return [];
        }

        return [.. rules
            .Where(r => r.Status == "Enabled" && Selects(r, version))
            .GroupBy(r => r.Destination?.Bucket, StringComparer.Ordinal)
            .Select(g => g.OrderByDescending(r => r.Priority ?? 0).First())];
    }

    /// <summary>Whether a rule's filter (or, in the first schema, its prefix) selects the version's key and tags.</summary>
    private static bool Selects(ReplicationRule rule, S3ObjectVersion version)
    {
        var filter = rule.Filter;
        var prefix = filter is null ? rule.Prefix : filter.Prefix ?? filter.And?.Prefix;
        if (!string.IsNullOrEmpty(prefix) && !version.Key.StartsWith(prefix!, StringComparison.Ordinal))
        {
            return false;
        }

        var tags = filter?.Tag is { } tag ? [tag] : filter?.And?.Tags ?? [];
        return tags.TrueForAll(t => version.Tags.Exists(v => v.Key == t.Key && v.Value == t.Value));
    }

    private static bool HasTagFilter(ReplicationRule rule) =>
        rule.Filter?.Tag is not null || rule.Filter?.And?.Tags is { Count: > 0 };

    /// <summary>The bucket a rule's destination ARN names, if it exists.</summary>
    private S3Bucket? DestinationBucket(ReplicationRule rule)
    {
        const string prefix = "arn:aws:s3:::";
        var arn = rule.Destination?.Bucket;
        return arn is not null && arn.StartsWith(prefix, StringComparison.Ordinal)
            && Store.Buckets.TryGetValue(arn.Substring(prefix.Length), out var bucket)
            ? bucket
            : null;
    }

    private static void ValidateReplicationRule(ReplicationRule rule)
    {
        if (rule.Status is not ("Enabled" or "Disabled") || rule.Destination?.Bucket is null)
        {
            throw S3Errors.MalformedXml();
        }

        if (rule.Filter is not null && rule.DeleteMarkerReplication is null)
        {
            throw S3Errors.InvalidRequest(
                "DeleteMarkerReplication must be specified for this version of Cross Region Replication configuration schema. " +
                "Please refer to S3 Developer Guide for more information.");
        }

        if (!rule.Destination.Bucket.StartsWith("arn:aws:s3:::", StringComparison.Ordinal))
        {
            throw S3Errors.InvalidArgument("Invalid bucket ARN.");
        }

        if (rule.Destination.ReplicationTime is not null && rule.Destination.Metrics is null)
        {
            throw S3Errors.InvalidRequest("Replication destination must contain both ReplicationTime and Metrics or neither.");
        }
    }

    /// <summary>A replica stored in the storage class its rule's destination names.</summary>
    private static S3ObjectVersion WithStorageClass(S3ObjectVersion replica, string storageClass) => new()
    {
        Key = replica.Key,
        VersionId = replica.VersionId,
        IsDeleteMarker = replica.IsDeleteMarker,
        Data = replica.Data,
        ETag = replica.ETag,
        LastModified = replica.LastModified,
        ContentType = replica.ContentType,
        ContentEncoding = replica.ContentEncoding,
        ContentDisposition = replica.ContentDisposition,
        ContentLanguage = replica.ContentLanguage,
        CacheControl = replica.CacheControl,
        Expires = replica.Expires,
        WebsiteRedirectLocation = replica.WebsiteRedirectLocation,
        Metadata = replica.Metadata,
        StorageClass = storageClass,
        ServerSideEncryption = replica.ServerSideEncryption,
        SseKmsKeyId = replica.SseKmsKeyId,
        BucketKeyEnabled = replica.BucketKeyEnabled,
        SseCustomerKeyMd5 = replica.SseCustomerKeyMd5,
        ChecksumAlgorithm = replica.ChecksumAlgorithm,
        Checksum = replica.Checksum,
        ChecksumType = replica.ChecksumType,
        Parts = replica.Parts,
        Tags = replica.Tags,
        OwnerId = replica.OwnerId,
        ReplicationStatus = replica.ReplicationStatus,
    };
}
