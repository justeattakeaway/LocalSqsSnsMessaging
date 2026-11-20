#pragma warning disable SA1402 // The client's Object Lock operations and the state they keep on buckets, versions and uploads.
#pragma warning disable SA1649
using System.Globalization;
using System.Net;
using InMemoryAws.Http;
using InMemoryAws.S3.Model;

namespace InMemoryAws.S3;

/// <summary>
/// Object Lock: a bucket's lock configuration and default retention, each version's retention
/// (<c>GOVERNANCE</c> or <c>COMPLIANCE</c> until a date), legal hold and event hold, the lock
/// headers of PutObject, CopyObject and CreateMultipartUpload, and the refusal to delete a
/// protected version.
/// </summary>
/// <remarks>
/// Every request acts as the account's root user, so <c>x-amz-bypass-governance-retention</c> is
/// always permitted: it lifts a <c>GOVERNANCE</c> retention, never a <c>COMPLIANCE</c> one, a legal
/// hold or an event hold.
/// </remarks>
internal sealed partial class InternalS3Client
{
    private const string MissingLockConfiguration = "Bucket is missing Object Lock Configuration";

    private const string MissingLockConfigurationForWrite = "Bucket is missing ObjectLockConfiguration";

    private static S3Exception ProtectedByObjectLock() =>
        S3Errors.AccessDenied("Access Denied because object protected by object lock.");

    private static S3Exception NoSuchObjectLockConfiguration() =>
        new("NoSuchObjectLockConfiguration", "The specified object does not have a ObjectLock configuration", HttpStatusCode.NotFound);

    /// <summary>
    /// Sets a bucket's Object Lock configuration: Object Lock on (it can't be turned off), and an
    /// optional default retention (a mode and a period in days or years) new versions get. A
    /// bucket created without Object Lock takes one once versioning is enabled.
    /// </summary>
    public override Task<PutObjectLockConfigurationOutput> PutObjectLockConfigurationAsync(PutObjectLockConfigurationRequest request, CancellationToken cancellationToken)
    {
        RequireContentDigest();
        var configuration = request.ObjectLockConfiguration ?? throw S3Errors.MalformedXml();
        if (configuration.ObjectLockEnabled != "Enabled")
        {
            throw S3Errors.MalformedXml();
        }

        if (configuration.Rule is { } rule)
        {
            var retention = rule.DefaultRetention ?? throw S3Errors.MalformedXml();
            if (retention.Mode is not ("GOVERNANCE" or "COMPLIANCE") || (retention.Days is null) == (retention.Years is null))
            {
                throw S3Errors.MalformedXml();
            }

            if (retention.Days is <= 0 || retention.Years is <= 0)
            {
                throw S3Errors.InvalidArgument("Default retention period must be a positive integer value.", "DefaultRetention", (retention.Days ?? retention.Years)!.Value.ToString(CultureInfo.InvariantCulture));
            }
        }

        lock (Store.Gate)
        {
            var bucket = GetBucket(request.Bucket);
            if (!bucket.ObjectLockEnabled && !bucket.VersioningEnabled)
            {
                throw new S3Exception("InvalidBucketState", "Versioning must be 'Enabled' on the bucket to apply a Object Lock configuration", HttpStatusCode.Conflict);
            }

            bucket.ObjectLockEnabled = true;
            bucket.DefaultRetention = configuration.Rule?.DefaultRetention is { } defaults
                ? new DefaultRetention { Mode = defaults.Mode, Days = defaults.Days, Years = defaults.Years }
                : null;
            return Task.FromResult(new PutObjectLockConfigurationOutput());
        }
    }

    /// <summary>The bucket's Object Lock configuration; a bucket without Object Lock has none.</summary>
    public override Task<GetObjectLockConfigurationOutput> GetObjectLockConfigurationAsync(GetObjectLockConfigurationRequest request, CancellationToken cancellationToken)
    {
        lock (Store.Gate)
        {
            var bucket = GetBucket(request.Bucket);
            if (!bucket.ObjectLockEnabled)
            {
                throw new S3Exception("ObjectLockConfigurationNotFoundError", "Object Lock configuration does not exist for this bucket", HttpStatusCode.NotFound, ("BucketName", bucket.Name));
            }

            return Task.FromResult(new GetObjectLockConfigurationOutput
            {
                ObjectLockConfiguration = new ObjectLockConfiguration
                {
                    ObjectLockEnabled = "Enabled",
                    Rule = bucket.DefaultRetention is { } defaults
                        ? new ObjectLockRule { DefaultRetention = new DefaultRetention { Mode = defaults.Mode, Days = defaults.Days, Years = defaults.Years } }
                        : null,
                },
            });
        }
    }

    /// <summary>
    /// Sets (or, with an empty <c>Retention</c>, clears) a version's retention. While a retention is in
    /// force it can only be strengthened (a later date, or <c>GOVERNANCE</c> to <c>COMPLIANCE</c>):
    /// weakening a <c>GOVERNANCE</c> retention takes <c>x-amz-bypass-governance-retention</c>, and a
    /// <c>COMPLIANCE</c> one can't be weakened at all.
    /// </summary>
    public override Task<PutObjectRetentionOutput> PutObjectRetentionAsync(PutObjectRetentionRequest request, CancellationToken cancellationToken)
    {
        RequireContentDigest();
        var retention = request.Retention ?? new ObjectLockRetention();
        if (retention.Mode is not (null or "GOVERNANCE" or "COMPLIANCE") || retention.EventHold is not (null or "ON" or "OFF")
            || (retention.Mode is null) != (retention.RetainUntilDate is null))
        {
            throw S3Errors.MalformedXml();
        }

        lock (Store.Gate)
        {
            var bucket = GetBucket(request.Bucket);
            RequireObjectLock(bucket);
            var version = LockTarget(bucket, request.Key, request.VersionId);
            var until = retention.RetainUntilDate is { } date ? TruncateToMilliseconds(date) : (DateTime?)null;
            var sameRetention = retention.Mode == version.RetentionMode && until == version.RetainUntil;
            if (until is { } newUntil && newUntil <= Now && !sameRetention)
            {
                throw S3Errors.InvalidArgument("The retain until date must be in the future!", "RetainUntilDate", S3Wire.FormatTimestamp(newUntil, "iso8601"));
            }

            if (version.RetentionInForce(Now) && Weakens(version, retention.Mode, until))
            {
                if (version.RetentionMode == "COMPLIANCE" || request.BypassGovernanceRetention != true)
                {
                    throw ProtectedByObjectLock();
                }

                DenyBypass(bucket, version);
            }

            version.RetentionMode = retention.Mode;
            version.RetainUntil = until;
            if (retention.EventHold is { } eventHold)
            {
                version.EventHold = eventHold;
            }

            if (retention.EventHoldDuration is { } duration)
            {
                version.EventHoldDays = duration.Days;
                version.EventHoldYears = duration.Years;
            }

            return Task.FromResult(new PutObjectRetentionOutput());
        }
    }

    /// <summary>A version's retention: <c>NoSuchObjectLockConfiguration</c> when it has none.</summary>
    public override Task<GetObjectRetentionOutput> GetObjectRetentionAsync(GetObjectRetentionRequest request, CancellationToken cancellationToken)
    {
        lock (Store.Gate)
        {
            var bucket = GetBucket(request.Bucket);
            RequireObjectLock(bucket);
            var version = LockTarget(bucket, request.Key, request.VersionId);
            if (version.RetentionMode is null && version.EventHold is null)
            {
                throw NoSuchObjectLockConfiguration();
            }

            return Task.FromResult(new GetObjectRetentionOutput
            {
                Retention = new ObjectLockRetention
                {
                    Mode = version.RetentionMode,
                    RetainUntilDate = version.RetainUntil,
                    EventHold = version.EventHold,
                    EventHoldDuration = version.EventHoldDays is null && version.EventHoldYears is null
                        ? null
                        : new EventHoldDuration { Days = version.EventHoldDays, Years = version.EventHoldYears },
                },
            });
        }
    }

    /// <summary>Turns a version's legal hold on or off.</summary>
    public override Task<PutObjectLegalHoldOutput> PutObjectLegalHoldAsync(PutObjectLegalHoldRequest request, CancellationToken cancellationToken)
    {
        RequireContentDigest();
        var status = request.LegalHold?.Status;
        if (status is not ("ON" or "OFF"))
        {
            throw S3Errors.MalformedXml();
        }

        lock (Store.Gate)
        {
            var bucket = GetBucket(request.Bucket);
            RequireObjectLock(bucket);
            var version = LockTarget(bucket, request.Key, request.VersionId);
            version.LegalHold = status;
            return Task.FromResult(new PutObjectLegalHoldOutput());
        }
    }

    /// <summary>A version's legal hold: <c>NoSuchObjectLockConfiguration</c> when one was never set.</summary>
    public override Task<GetObjectLegalHoldOutput> GetObjectLegalHoldAsync(GetObjectLegalHoldRequest request, CancellationToken cancellationToken)
    {
        lock (Store.Gate)
        {
            var bucket = GetBucket(request.Bucket);
            RequireObjectLock(bucket);
            var version = LockTarget(bucket, request.Key, request.VersionId);
            if (version.LegalHold is null)
            {
                throw NoSuchObjectLockConfiguration();
            }

            return Task.FromResult(new GetObjectLegalHoldOutput { LegalHold = new ObjectLockLegalHold { Status = version.LegalHold } });
        }
    }

    /// <summary>
    /// A version that is under legal hold, under an event hold, or retained until a date still to
    /// come is refused: <c>COMPLIANCE</c> always, <c>GOVERNANCE</c> unless the request bypasses it.
    /// </summary>
    partial void CheckDeletableCore(S3Bucket bucket, S3ObjectVersion version, bool bypassGovernance)
    {
        if (version.IsDeleteMarker)
        {
            return;
        }

        if (version.LegalHold == "ON" || version.EventHold == "ON")
        {
            throw ProtectedByObjectLock();
        }

        if (version.RetentionInForce(Now) && (version.RetentionMode == "COMPLIANCE" || !bypassGovernance))
        {
            throw ProtectedByObjectLock();
        }

        if (version.RetentionInForce(Now))
        {
            DenyBypass(bucket, version);
        }
    }

    /// <summary>
    /// Bypassing a <c>GOVERNANCE</c> retention takes <c>s3:BypassGovernanceRetention</c> on the object:
    /// a bucket policy that explicitly denies it to the caller refuses the bypass.
    /// </summary>
    private void DenyBypass(S3Bucket bucket, S3ObjectVersion version) =>
        DenyExplicitly(bucket, "BypassGovernanceRetention", $"arn:aws:s3:::{bucket.Name}/{version.Key}");

#pragma warning disable CA1822 // The hook is declared on the instance; other areas may use its state.
    partial void DescribeMore(S3Bucket bucket, S3ObjectVersion version, IS3ObjectHeaders output) =>
        DescribeObjectLock(version, output);
#pragma warning restore CA1822

    /// <summary>The <c>x-amz-object-lock-*</c> headers GetObject and HeadObject answer for a version.</summary>
    private static void DescribeObjectLock(S3ObjectVersion version, IS3ObjectHeaders output)
    {
        if (output is not IS3ObjectLockMembers lockOutput)
        {
            return;
        }

        lockOutput.ObjectLockMode = version.RetentionMode;
        lockOutput.ObjectLockRetainUntilDate = version.RetainUntil;
        lockOutput.ObjectLockLegalHoldStatus = version.LegalHold;
        lockOutput.ObjectLockEventHold = version.EventHold;
        lockOutput.ObjectLockEventHoldDurationDays = version.EventHoldDays;
        lockOutput.ObjectLockEventHoldDurationYears = version.EventHoldYears;
    }

    /// <summary>
    /// Applies a PutObject's or CopyObject's lock headers to the new version, or else the bucket's
    /// default retention; called before the version is stored, so a refusal stores nothing.
    /// </summary>
    partial void ApplyObjectLock(S3Bucket bucket, S3ObjectVersion version, IS3ObjectLockMembers request)
    {
        var settings = ObjectLockSettings.From(request);
        if (settings.Any)
        {
            if (!bucket.ObjectLockEnabled)
            {
                throw S3Errors.InvalidRequest(MissingLockConfigurationForWrite);
            }

            if (request is PutObjectRequest)
            {
                RequireContentDigest("Content-MD5 OR x-amz-checksum- HTTP header is required for Put Object requests with Object Lock parameters");
            }

            CheckLockHeaders(settings);
        }

        Apply(bucket, version, settings);
    }

    partial void OnUploadCreated(S3Bucket bucket, S3MultipartUpload upload, CreateMultipartUploadRequest request) =>
        CreateUploadObjectLock(bucket, upload, request);

    /// <summary>Checks a CreateMultipartUpload's lock headers and keeps them for the object it completes into.</summary>
    private void CreateUploadObjectLock(S3Bucket bucket, S3MultipartUpload upload, CreateMultipartUploadRequest request)
    {
        var settings = ObjectLockSettings.From(request);
        if (!settings.Any)
        {
            return;
        }

        if (!bucket.ObjectLockEnabled)
        {
            throw S3Errors.InvalidRequest(MissingLockConfigurationForWrite);
        }

        CheckLockHeaders(settings);
        upload.ObjectLock = settings;
    }

    partial void OnUploadCompleting(S3Bucket bucket, S3MultipartUpload upload, S3ObjectVersion version) =>
        Apply(bucket, version, upload.ObjectLock ?? default);

    /// <summary>
    /// Gives a new version the lock settings its request asked for, or, with no retention asked for, the
    /// bucket's default retention dated from now.
    /// </summary>
    private void Apply(S3Bucket bucket, S3ObjectVersion version, ObjectLockSettings settings)
    {
        if (settings.Mode is not null)
        {
            version.RetentionMode = settings.Mode;
            version.RetainUntil = settings.RetainUntil;
        }
        else if (bucket.ObjectLockEnabled && bucket.DefaultRetention is { } defaults)
        {
            var now = TruncateToMilliseconds(Now);
            version.RetentionMode = defaults.Mode;
            version.RetainUntil = defaults.Days is { } days ? now.AddDays(days) : now.AddYears(defaults.Years ?? 0);
        }

        version.LegalHold = settings.LegalHold;
        version.EventHold = settings.EventHold;
        version.EventHoldDays = settings.EventHoldDays;
        version.EventHoldYears = settings.EventHoldYears;
    }

    /// <summary>The lock headers' own rules, as S3 checks them on a lock-enabled bucket.</summary>
    private void CheckLockHeaders(ObjectLockSettings settings)
    {
        if (settings.Mode is not (null or "GOVERNANCE" or "COMPLIANCE"))
        {
            throw S3Errors.InvalidArgument("Unknown wormMode directive.", "x-amz-object-lock-mode", settings.Mode);
        }

        if (settings.LegalHold is not (null or "ON" or "OFF"))
        {
            throw S3Errors.InvalidArgument("Legal Hold must be either of 'ON' or 'OFF'", "x-amz-object-lock-legal-hold", settings.LegalHold);
        }

        if (settings.EventHold is not null)
        {
            if (settings.Mode is null)
            {
                throw S3Errors.InvalidRequest("x-amz-object-lock-mode is required when x-amz-object-lock-event-hold is supplied.");
            }

            if (settings.EventHold == "ON" && (settings.EventHoldDays is null) == (settings.EventHoldYears is null))
            {
                throw S3Errors.InvalidRequest("x-amz-object-lock-event-hold:ON must be supplied with exactly one event hold duration (days or years).");
            }
        }

        if ((settings.Mode is null) != (settings.RetainUntil is null))
        {
            throw S3Errors.InvalidArgument("x-amz-object-lock-retain-until-date and x-amz-object-lock-mode must both be supplied", "x-amz-object-lock-retain-until-date", null);
        }

        if (settings.RetainUntil is { } until)
        {
            var now = Now;
            if (until <= now)
            {
                throw S3Errors.InvalidArgument("The retain until date must be in the future!", "x-amz-object-lock-retain-until-date", until.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture));
            }

            if (settings.EventHold == "ON")
            {
                var floor = settings.EventHoldDays is { } days ? now.AddDays(days) : now.AddYears(settings.EventHoldYears ?? 0);
                if (until < floor)
                {
                    throw S3Errors.InvalidArgument("The retain until date must be equal to or later than the current time plus the event hold duration.", "x-amz-object-lock-retain-until-date", until.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture));
                }
            }
        }
    }

    /// <summary>Whether a new retention is weaker than the version's: cleared, shortened, or moved from <c>COMPLIANCE</c> to <c>GOVERNANCE</c>.</summary>
    private static bool Weakens(S3ObjectVersion version, string? mode, DateTime? until) =>
        mode is null
        || (version.RetentionMode == "COMPLIANCE" && mode != "COMPLIANCE")
        || until < version.RetainUntil;

    /// <summary>The Object Lock subresources need a bucket with Object Lock.</summary>
    private static void RequireObjectLock(S3Bucket bucket)
    {
        if (!bucket.ObjectLockEnabled)
        {
            throw S3Errors.InvalidRequest(MissingLockConfiguration);
        }
    }

    /// <summary>The version an Object Lock subresource request names: the current one, or the one its version id names.</summary>
    private S3ObjectVersion LockTarget(S3Bucket bucket, string? keyName, string? versionId)
    {
        var key = CheckKey(keyName);
        if (versionId is not null)
        {
            CheckVersionIdFormat(versionId);
            var version = bucket.Version(key, versionId) ?? throw S3Errors.NoSuchVersion(key, versionId);
            return version.IsDeleteMarker ? throw S3Errors.MethodNotAllowed(Request?.Method ?? "PUT", "DeleteMarker") : version;
        }

        var current = bucket.Current(key);
        return current is null || current.IsDeleteMarker ? throw S3Errors.NoSuchKey(key) : current;
    }

    /// <summary>
    /// Requests S3 takes only with a digest of their body (<c>Content-MD5</c> or an <c>x-amz-checksum-*</c>
    /// header or trailer) are refused without one. A request made without HTTP is not checked.
    /// </summary>
    private void RequireContentDigest(string message = "Missing required header for this request: Content-MD5")
    {
        if (Request is not { } wire || wire.Header("Content-MD5") is not null
            || wire.Headers.Keys.Any(name => name.StartsWith("x-amz-checksum-", StringComparison.OrdinalIgnoreCase) && !name.Equals("x-amz-checksum-type", StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        throw S3Errors.InvalidRequest(message);
    }

    private static DateTime TruncateToMilliseconds(DateTime value)
    {
        var utc = value.Kind == DateTimeKind.Local ? value.ToUniversalTime() : value;
        return new DateTime(utc.Ticks - (utc.Ticks % TimeSpan.TicksPerMillisecond), DateTimeKind.Utc);
    }
}

/// <summary>The Object Lock headers a write asks for.</summary>
internal readonly record struct ObjectLockSettings(
    string? Mode,
    DateTime? RetainUntil,
    string? LegalHold,
    string? EventHold,
    int? EventHoldDays,
    int? EventHoldYears)
{
    /// <summary>Whether the write sent any lock header.</summary>
    public bool Any => Mode is not null || RetainUntil is not null || LegalHold is not null || EventHold is not null
        || EventHoldDays is not null || EventHoldYears is not null;

    public static ObjectLockSettings From(IS3ObjectLockMembers request) => new(
        request.ObjectLockMode,
        request.ObjectLockRetainUntilDate is { } until
            ? new DateTime(until.Ticks - (until.Ticks % TimeSpan.TicksPerMillisecond), DateTimeKind.Utc)
            : null,
        request.ObjectLockLegalHoldStatus,
        request.ObjectLockEventHold,
        request.ObjectLockEventHoldDurationDays,
        request.ObjectLockEventHoldDurationYears);
}

/// <summary>A bucket's default retention, set by PutObjectLockConfiguration.</summary>
internal sealed partial class S3Bucket
{
    /// <summary>The retention new versions get when their write asks for none; null for none.</summary>
    public DefaultRetention? DefaultRetention { get; set; }
}

/// <summary>A version's Object Lock state, which S3 changes in place.</summary>
internal sealed partial class S3ObjectVersion
{
    /// <summary><c>GOVERNANCE</c> or <c>COMPLIANCE</c>; null for a version with no retention.</summary>
    public string? RetentionMode { get; set; }

    public DateTime? RetainUntil { get; set; }

    /// <summary><c>ON</c> or <c>OFF</c> once set; null for a version whose legal hold was never set.</summary>
    public string? LegalHold { get; set; }

    /// <summary><c>ON</c> or <c>OFF</c> once set; null for a version never put under an event hold.</summary>
    public string? EventHold { get; set; }

    public int? EventHoldDays { get; set; }

    public int? EventHoldYears { get; set; }

    /// <summary>Whether the version's retention date is still to come.</summary>
    public bool RetentionInForce(DateTime now) => RetentionMode is not null && RetainUntil > now;
}

/// <summary>The Object Lock headers of a CreateMultipartUpload, for the object it completes into.</summary>
internal sealed partial class S3MultipartUpload
{
    public ObjectLockSettings? ObjectLock { get; set; }
}
