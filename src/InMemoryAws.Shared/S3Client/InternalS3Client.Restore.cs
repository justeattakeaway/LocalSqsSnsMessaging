using System.Globalization;
using System.Net;
using InMemoryAws.S3.Model;

namespace InMemoryAws.S3;

/// <summary>
/// Archived objects and their restores. An object in <c>GLACIER</c> or <c>DEEP_ARCHIVE</c> can be
/// listed and described, but its data can't be read or copied until RestoreObject makes a temporary
/// copy of it available.
/// </summary>
/// <remarks>
/// A restore runs on the cloud's clock: it is ongoing for the retrieval tier's time (for
/// <c>GLACIER</c>: Expedited 1 minute, Standard 3 hours, Bulk 5 hours; for <c>DEEP_ARCHIVE</c>:
/// Standard 12 hours, Bulk 48 hours, the fastest times AWS quotes), then the copy is readable until
/// its expiry date: its days after the restore completed, rounded up to the next midnight UTC. After
/// that the object is archived again. Nothing runs on a timer: a restore's state is worked out from
/// the clock whenever the object is read.
/// </remarks>
internal sealed partial class InternalS3Client
{
    private static readonly string[] ArchivedStorageClasses = ["GLACIER", "DEEP_ARCHIVE"];

    /// <summary>
    /// The status the response gets in place of the operation's usual one: 202 for a restore
    /// RestoreObject started (or sped up), where an already restored copy answers 200.
    /// </summary>
    internal int? ResponseStatus { get; private set; }

    /// <summary>
    /// Starts restoring an archived object (202), speeds up a restore in progress when a faster tier
    /// is asked for (202), or sets a restored copy's expiry date again from now (200). Asking again
    /// for the same or a slower tier while a restore is in progress is <c>RestoreAlreadyInProgress</c>;
    /// an object that is not archived is <c>InvalidObjectState</c>.
    /// </summary>
    public override Task<RestoreObjectOutput> RestoreObjectAsync(RestoreObjectRequest request, CancellationToken cancellationToken)
    {
        var restore = request.RestoreRequest ?? throw S3Errors.MalformedXml();
        if (restore.Type is not null)
        {
            throw S3Errors.NotImplemented("RestoreObject with a SELECT request");
        }

        var days = restore.Days ?? throw S3Errors.MalformedXml();
        if (days < 1)
        {
            throw S3Errors.InvalidArgument("restoration days should be at least 1");
        }

        var tier = restore.GlacierJobParameters?.Tier ?? restore.Tier ?? "Standard";
        if (tier is not ("Expedited" or "Standard" or "Bulk"))
        {
            throw S3Errors.MalformedXml();
        }

        lock (Store.Gate)
        {
            var bucket = GetBucket(request.Bucket);
            var key = CheckKey(request.Key);
            var version = RestoreTarget(bucket, key, request.VersionId);
            if (Array.IndexOf(ArchivedStorageClasses, version.StorageClass) < 0)
            {
                throw new S3Exception("InvalidObjectState", "Restore is not allowed for the object's current storage class", HttpStatusCode.Forbidden);
            }

            if (version.StorageClass == "DEEP_ARCHIVE" && tier == "Expedited")
            {
                throw new S3Exception("InvalidTier", "Retrieval option is not supported by this storage class.", HttpStatusCode.BadRequest);
            }

            var now = Now;
            var state = SettleRestore(version, now);
            if (state is null)
            {
                version.Restore = new S3Restore(tier, days, now + RetrievalTime(version.StorageClass, tier));
                ResponseStatus = 202;
                NotifyObjectEvent(bucket, version, "ObjectRestore:Post");
            }
            else if (state.Ongoing)
            {
                var completes = now + RetrievalTime(version.StorageClass, tier);
                if (TierRank(tier) <= TierRank(state.Tier) || completes >= state.CompletesAt)
                {
                    throw new S3Exception("RestoreAlreadyInProgress", "Object restore is already in progress", HttpStatusCode.Conflict);
                }

                version.Restore = new S3Restore(tier, days, completes);
                ResponseStatus = 202;
            }
            else
            {
                version.Restore = state.Expiring(RestoreExpiry(now, days));
                ResponseStatus = 200;
            }

            return Task.FromResult(new RestoreObjectOutput());
        }
    }

    /// <summary>
    /// The <c>x-amz-restore</c> header for an object: whether a restore is ongoing, and once it is
    /// done, when the restored copy expires; null for an object never restored, or whose copy expired.
    /// </summary>
    private string? RestoreHeader(S3ObjectVersion version)
    {
        if (SettleRestore(version, Now) is not { } state)
        {
            return null;
        }

        return state.Ongoing
            ? "ongoing-request=\"true\""
            : "ongoing-request=\"false\", expiry-date=\"" + Http.S3Wire.FormatTimestamp(state.ExpiresAt!.Value, "rfc822") + "\"";
    }

    /// <summary>GetObject on an archived object whose data isn't restored is <c>InvalidObjectState</c>.</summary>
    private void CheckArchivedRead(S3ObjectVersion version)
    {
        if (!IsReadable(version))
        {
            throw new S3Exception("InvalidObjectState", "The operation is not valid for the object's storage class", HttpStatusCode.Forbidden,
                ("StorageClass", version.StorageClass));
        }
    }

    /// <summary>A copy from an archived object whose data isn't restored is <c>InvalidObjectState</c>.</summary>
    private void CheckArchivedCopySource(S3ObjectVersion source)
    {
        if (!IsReadable(source))
        {
            throw new S3Exception("InvalidObjectState", "Operation is not valid for the source object's storage class", HttpStatusCode.Forbidden,
                ("StorageClass", source.StorageClass));
        }
    }

    /// <summary>Whether an object's data can be read: it is not archived, or a restored copy of it is available.</summary>
    private bool IsReadable(S3ObjectVersion version) =>
        Array.IndexOf(ArchivedStorageClasses, version.StorageClass) < 0
        || SettleRestore(version, Now) is { Ongoing: false };

    /// <summary>
    /// The object's restore as of <paramref name="now"/>: completed once its retrieval time has
    /// passed, and gone (the object archived again) once its copy has expired.
    /// </summary>
    private static S3Restore? SettleRestore(S3ObjectVersion version, DateTime now)
    {
        var state = version.Restore;
        if (state is null)
        {
            return null;
        }

        if (state.Ongoing && now >= state.CompletesAt)
        {
            state = state.Expiring(RestoreExpiry(state.CompletesAt, state.Days));
            version.Restore = state;
        }

        if (!state.Ongoing && now >= state.ExpiresAt)
        {
            version.Restore = null;
            return null;
        }

        return state;
    }

    /// <summary>The version RestoreObject restores: the current one, or the one named; never a delete marker.</summary>
    private static S3ObjectVersion RestoreTarget(S3Bucket bucket, string key, string? versionId)
    {
        if (versionId is not null)
        {
            CheckVersionIdFormat(versionId);
            var version = bucket.Version(key, versionId) ?? throw S3Errors.NoSuchVersion(key, versionId);
            return version.IsDeleteMarker ? throw S3Errors.MethodNotAllowed("POST", "DeleteMarker") : version;
        }

        var current = bucket.Current(key);
        return current is null || current.IsDeleteMarker ? throw S3Errors.NoSuchKey(key) : current;
    }

    /// <summary>How long a restore from <paramref name="storageClass"/> takes at <paramref name="tier"/>.</summary>
    private static TimeSpan RetrievalTime(string storageClass, string tier) => (storageClass, tier) switch
    {
        ("GLACIER", "Expedited") => TimeSpan.FromMinutes(1),
        ("GLACIER", "Standard") => TimeSpan.FromHours(3),
        ("GLACIER", _) => TimeSpan.FromHours(5),
        (_, "Standard") => TimeSpan.FromHours(12),
        _ => TimeSpan.FromHours(48),
    };

    /// <summary>A tier's speed: Bulk is slowest, Expedited fastest.</summary>
    private static int TierRank(string tier) => tier switch
    {
        "Bulk" => 0,
        "Standard" => 1,
        _ => 2,
    };

    /// <summary><paramref name="days"/> after <paramref name="from"/>, rounded up to the next midnight UTC.</summary>
    private static DateTime RestoreExpiry(DateTime from, int days) =>
        DateTime.SpecifyKind(from.AddDays(days).Date.AddDays(1), DateTimeKind.Utc);
}

/// <summary>
/// A restore of an archived object: in progress until <see cref="CompletesAt"/>, then a readable copy
/// until <see cref="ExpiresAt"/>. Immutable; a change replaces it.
/// </summary>
internal sealed class S3Restore
{
    public S3Restore(string tier, int days, DateTime completesAt, DateTime? expiresAt = null)
    {
        Tier = tier;
        Days = days;
        CompletesAt = completesAt;
        ExpiresAt = expiresAt;
    }

    /// <summary>The retrieval tier: <c>Expedited</c>, <c>Standard</c> or <c>Bulk</c>.</summary>
    public string Tier { get; }

    /// <summary>How many days the restored copy stays available.</summary>
    public int Days { get; }

    /// <summary>When the restore completes.</summary>
    public DateTime CompletesAt { get; }

    /// <summary>When the restored copy expires; null while the restore is ongoing.</summary>
    public DateTime? ExpiresAt { get; }

    /// <summary>Whether the restore is still in progress.</summary>
    public bool Ongoing => ExpiresAt is null;

    /// <summary>This restore, completed, with its copy expiring at <paramref name="expiresAt"/>.</summary>
    public S3Restore Expiring(DateTime expiresAt) => new(Tier, Days, CompletesAt, expiresAt);
}

/// <summary>An object version's restore.</summary>
internal sealed partial class S3ObjectVersion
{
    /// <summary>The object's restore, while one is in progress or its restored copy is available.</summary>
    public S3Restore? Restore { get; set; }
}
