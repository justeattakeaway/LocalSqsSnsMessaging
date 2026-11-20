using System.Globalization;
using System.Net;
using InMemoryAws.S3.Model;

namespace InMemoryAws.S3;

/// <summary>
/// Bucket lifecycle configuration: kept as sent once it passes S3's checks, and answered back. The
/// cloud never runs a rule (nothing expires, transitions or is aborted), but it stamps what S3 stamps
/// from the rules: <c>x-amz-expiration</c> on an object an expiration rule covers, and
/// <c>x-amz-abort-date</c>/<c>x-amz-abort-rule-id</c> on a multipart upload an
/// <c>AbortIncompleteMultipartUpload</c> rule covers.
/// </summary>
internal sealed partial class InternalS3Client
{
    /// <summary>The value S3 answers for <c>x-amz-transition-default-minimum-object-size</c> when none was sent.</summary>
    private const string DefaultTransitionMinimumObjectSize = "all_storage_classes_128K";

    /// <summary>The storage classes a <c>Transition</c> may move objects to, from warmest to coldest.</summary>
    private static readonly string[] TransitionClasses =
        ["INTELLIGENT_TIERING", "STANDARD_IA", "ONEZONE_IA", "GLACIER_IR", "GLACIER", "DEEP_ARCHIVE"];

    public override Task<PutBucketLifecycleConfigurationOutput> PutBucketLifecycleConfigurationAsync(PutBucketLifecycleConfigurationRequest request, CancellationToken cancellationToken)
    {
        var minimumSize = request.TransitionDefaultMinimumObjectSize ?? DefaultTransitionMinimumObjectSize;
        if (minimumSize is not ("all_storage_classes_128K" or "varies_by_storage_class"))
        {
            throw S3Errors.InvalidRequest("Invalid TransitionDefaultMinimumObjectSize found");
        }

        lock (Store.Gate)
        {
            var bucket = GetBucket(request.Bucket);
            var rules = request.LifecycleConfiguration?.Rules;
            if (rules is null || rules.Count is 0 or > 1000)
            {
                throw S3Errors.MalformedXml();
            }

            CheckLifecycleRules(rules);
            bucket.LifecycleRules = rules;
            bucket.TransitionDefaultMinimumObjectSize = minimumSize;
            return Task.FromResult(new PutBucketLifecycleConfigurationOutput { TransitionDefaultMinimumObjectSize = minimumSize });
        }
    }

    public override Task<GetBucketLifecycleConfigurationOutput> GetBucketLifecycleConfigurationAsync(GetBucketLifecycleConfigurationRequest request, CancellationToken cancellationToken)
    {
        lock (Store.Gate)
        {
            var bucket = GetBucket(request.Bucket);
            var rules = bucket.LifecycleRules ?? throw NoSuchLifecycleConfiguration(bucket);
            return Task.FromResult(new GetBucketLifecycleConfigurationOutput
            {
                Rules = rules,
                TransitionDefaultMinimumObjectSize = bucket.TransitionDefaultMinimumObjectSize ?? DefaultTransitionMinimumObjectSize,
            });
        }
    }

    public override Task<DeleteBucketLifecycleOutput> DeleteBucketLifecycleAsync(DeleteBucketLifecycleRequest request, CancellationToken cancellationToken)
    {
        lock (Store.Gate)
        {
            var bucket = GetBucket(request.Bucket);
            bucket.LifecycleRules = null;
            bucket.TransitionDefaultMinimumObjectSize = null;
            return Task.FromResult(new DeleteBucketLifecycleOutput());
        }
    }

    private static S3Exception NoSuchLifecycleConfiguration(S3Bucket bucket) =>
        S3Errors.NoSuchConfiguration("NoSuchLifecycleConfiguration", "The lifecycle configuration does not exist", bucket.Name);

    /// <summary>
    /// The <c>x-amz-expiration</c> header for a version: the earliest expiry of the enabled rules
    /// whose filter covers it and whose <c>Expiration</c> has days or a date, as
    /// <c>expiry-date="...", rule-id="..."</c>; null when no rule covers it. Only a current version
    /// that is not a delete marker expires this way.
    /// </summary>
    private static string? LifecycleExpiration(S3Bucket bucket, S3ObjectVersion version)
    {
        if (bucket.LifecycleRules is not { } rules || version.IsDeleteMarker || !ReferenceEquals(bucket.Current(version.Key), version))
        {
            return null;
        }

        (DateTime Date, string Id)? earliest = null;
        foreach (var rule in rules)
        {
            if (rule.Status != "Enabled" || rule.Expiration is not { } expiration || !RuleCovers(rule, version.Key, version.Size, version.Tags))
            {
                continue;
            }

            DateTime date;
            if (expiration.Days is { } days)
            {
                date = NextMidnight(version.LastModified.AddDays(days));
            }
            else if (expiration.Date is { } at)
            {
                date = at.Kind == DateTimeKind.Local ? at.ToUniversalTime() : at;
            }
            else
            {
                continue;
            }

            if (earliest is null || date < earliest.Value.Date)
            {
                earliest = (date, rule.ID ?? string.Empty);
            }
        }

        return earliest is { } found
            ? $"expiry-date=\"{Http.S3Wire.FormatTimestamp(found.Date, "rfc822")}\", rule-id=\"{found.Id}\""
            : null;
    }

    /// <summary>
    /// Stamps a new multipart upload with the abort date and rule of the enabled
    /// <c>AbortIncompleteMultipartUpload</c> rule that covers its key (the earliest, when several
    /// do), fixed when the upload is created.
    /// </summary>
    private static void ApplyAbortRule(S3Bucket bucket, S3MultipartUpload upload)
    {
        if (bucket.LifecycleRules is not { } rules)
        {
            return;
        }

        foreach (var rule in rules)
        {
            if (rule.Status != "Enabled" || rule.AbortIncompleteMultipartUpload?.DaysAfterInitiation is not { } days
                || !RuleCovers(rule, upload.Key, size: null, tags: null))
            {
                continue;
            }

            var date = NextMidnight(upload.Initiated.AddDays(days));
            if (upload.AbortDate is null || date < upload.AbortDate)
            {
                upload.AbortDate = date;
                upload.AbortRuleId = rule.ID;
            }
        }
    }

    /// <summary>S3 rounds a lifecycle date up to the next midnight UTC.</summary>
    private static DateTime NextMidnight(DateTime value) => DateTime.SpecifyKind(value.Date.AddDays(1), DateTimeKind.Utc);

    /// <summary>
    /// Whether a rule's filter (or its legacy <c>Prefix</c>) covers an object: its prefix, tags and
    /// size bounds all hold. An upload has no tags or size yet, so only the prefix is checked.
    /// </summary>
    private static bool RuleCovers(LifecycleRule rule, string key, long? size, List<Tag>? tags)
    {
        var filter = rule.Filter;
        var and = filter?.And;
        var prefix = rule.Prefix ?? filter?.Prefix ?? and?.Prefix ?? string.Empty;
        if (!key.StartsWith(prefix, StringComparison.Ordinal))
        {
            return false;
        }

        var wanted = new List<Tag>();
        if (filter?.Tag is { } tag)
        {
            wanted.Add(tag);
        }

        wanted.AddRange(and?.Tags ?? []);
        if (wanted.Count > 0 && (tags is null || !wanted.All(w => tags.Any(t => t.Key == w.Key && t.Value == w.Value))))
        {
            return false;
        }

        if (size is { } length)
        {
            if ((filter?.ObjectSizeGreaterThan ?? and?.ObjectSizeGreaterThan) is { } greater && length <= greater)
            {
                return false;
            }

            if ((filter?.ObjectSizeLessThan ?? and?.ObjectSizeLessThan) is { } less && length >= less)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Checks a lifecycle configuration's rules as S3 does, with its codes and messages.</summary>
    private static void CheckLifecycleRules(List<LifecycleRule> rules)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var rule in rules)
        {
            if (rule.Status is not ("Enabled" or "Disabled") || (rule.Filter is not null && rule.Prefix is not null))
            {
                throw S3Errors.MalformedXml();
            }

            if (rule.ID is { } id)
            {
                if (id.Length > 255)
                {
                    throw S3Errors.InvalidArgument("ID length should not exceed allowed limit of 255", "ID", id);
                }

                if (!ids.Add(id))
                {
                    throw S3Errors.InvalidArgument("Rule ID must be unique. Found same ID for more than one rule", "ID", id);
                }
            }

            CheckLifecycleFilter(rule.Filter);
            if (rule.Expiration is null && rule.Transitions is null && rule.NoncurrentVersionTransitions is null
                && rule.NoncurrentVersionExpiration is null && rule.AbortIncompleteMultipartUpload is null)
            {
                throw S3Errors.InvalidRequest("At least one action needs to be specified in a rule");
            }

            if (rule.Expiration is { } expiration)
            {
                if (expiration.Days is { } days && days <= 0)
                {
                    throw S3Errors.InvalidArgument("'Days' for Expiration action must be a positive integer");
                }

                if (expiration.ExpiredObjectDeleteMarker is not null && (expiration.Days is not null || expiration.Date is not null))
                {
                    throw S3Errors.InvalidArgument("ExpiredObjectDeleteMarker cannot be specified with Days or Date in a Lifecycle Expiration Policy.");
                }

                if (expiration.Days is not null && expiration.Date is not null)
                {
                    throw S3Errors.MalformedXml();
                }
            }

            if (rule.AbortIncompleteMultipartUpload is { } abort)
            {
                if (abort.DaysAfterInitiation is not { } days || days <= 0)
                {
                    throw S3Errors.InvalidArgument("'DaysAfterInitiation' for AbortIncompleteMultipartUpload action must be a positive integer");
                }

                if (rule.Filter?.Tag is not null || rule.Filter?.And?.Tags is { Count: > 0 })
                {
                    throw S3Errors.InvalidRequest("AbortIncompleteMultipartUpload cannot be specified with Tags.");
                }
            }

            if (rule.NoncurrentVersionExpiration is { } noncurrent)
            {
                if (noncurrent.NoncurrentDays is not { } days || days <= 0)
                {
                    throw S3Errors.InvalidArgument("'NoncurrentDays' for NoncurrentVersionExpiration action must be a positive integer");
                }

                if (noncurrent.NewerNoncurrentVersions is { } newer && newer is <= 0 or > 100)
                {
                    throw S3Errors.InvalidArgument("'NewerNoncurrentVersions' for NoncurrentVersionExpiration action must be a positive integer up to 100");
                }
            }

            foreach (var transition in rule.NoncurrentVersionTransitions ?? [])
            {
                if (Array.IndexOf(TransitionClasses, transition.StorageClass) < 0)
                {
                    throw S3Errors.InvalidArgument("Invalid target Storage class for NoncurrentVersionTransition action");
                }

                if (transition.NoncurrentDays is not { } days || days < 0)
                {
                    throw S3Errors.InvalidArgument("'NoncurrentDays' for NoncurrentVersionTransition action must be a nonnegative integer");
                }
            }

            CheckTransitions(rule);
        }
    }

    /// <summary>
    /// A rule's filter holds one predicate: a prefix, a tag, a size bound or an <c>And</c> of
    /// several. An <c>And</c>'s size range must be a real range.
    /// </summary>
    private static void CheckLifecycleFilter(LifecycleRuleFilter? filter)
    {
        if (filter is null)
        {
            return;
        }

        var members = (filter.Prefix is null ? 0 : 1) + (filter.Tag is null ? 0 : 1) + (filter.ObjectSizeGreaterThan is null ? 0 : 1)
            + (filter.ObjectSizeLessThan is null ? 0 : 1) + (filter.And is null ? 0 : 1);
        if (members > 1)
        {
            throw S3Errors.MalformedXml();
        }

        if (filter.ObjectSizeGreaterThan < 0 || filter.ObjectSizeLessThan <= 0)
        {
            throw S3Errors.InvalidRequest("'ObjectSizeGreaterThan' and 'ObjectSizeLessThan' must be non-negative.");
        }

        if (filter.And is { } and)
        {
            if (and.ObjectSizeGreaterThan is { } greater && and.ObjectSizeLessThan is { } less && less <= greater)
            {
                throw S3Errors.InvalidRequest("'ObjectSizeLessThan' has to be a value greater than 'ObjectSizeGreaterThan'.");
            }

            if (and.Tags is { } tags)
            {
                var keys = new HashSet<string>(StringComparer.Ordinal);
                if (tags.Any(t => !keys.Add(t.Key ?? string.Empty)))
                {
                    throw S3Errors.InvalidRequest("Duplicate Tag Keys are not allowed.");
                }
            }
        }
    }

    /// <summary>
    /// A rule's transitions: each to a class S3 transitions to, and in day order from warmer to
    /// colder classes, each a class's minimum storage duration after the one before (30 days after
    /// <c>STANDARD_IA</c> or <c>ONEZONE_IA</c>, 90 after <c>GLACIER_IR</c> or <c>GLACIER</c>).
    /// </summary>
    private static void CheckTransitions(LifecycleRule rule)
    {
        if (rule.Transitions is not { Count: > 0 } transitions)
        {
            return;
        }

        foreach (var transition in transitions)
        {
            if (Array.IndexOf(TransitionClasses, transition.StorageClass) < 0)
            {
                throw S3Errors.InvalidArgument("Invalid target Storage class for Transition action");
            }

            if (transition.Days is { } days)
            {
                if (days < 0)
                {
                    throw S3Errors.InvalidArgument("'Days' in Transition action must be nonnegative");
                }

                if (transition.StorageClass is "STANDARD_IA" or "ONEZONE_IA" && days < 30)
                {
                    throw S3Errors.InvalidArgument($"'Days' in Transition action must be greater than or equal to 30 for storageClass '{transition.StorageClass}'");
                }
            }
            else if (transition.Date is null)
            {
                throw S3Errors.MalformedXml();
            }
        }

        var filter = $"(prefix={rule.Prefix ?? rule.Filter?.Prefix ?? rule.Filter?.And?.Prefix ?? string.Empty})";
        var ordered = transitions.Where(t => t.Days is not null).OrderBy(t => t.Days).ToList();
        for (var i = 1; i < ordered.Count; i++)
        {
            var earlier = ordered[i - 1];
            var later = ordered[i];
            if (Array.IndexOf(TransitionClasses, later.StorageClass) <= Array.IndexOf(TransitionClasses, earlier.StorageClass))
            {
                throw S3Errors.InvalidArgument(
                    $"'Days' in the 'Transition' action for StorageClass '{earlier.StorageClass}' for filter '{filter}' must be greater than "
                    + $"'Days' in the 'Transition' action for StorageClass '{later.StorageClass}' for filter '{filter}'");
            }

            var minimum = earlier.StorageClass switch
            {
                "STANDARD_IA" or "ONEZONE_IA" => 30,
                "GLACIER_IR" or "GLACIER" => 90,
                _ => 0,
            };
            if (later.Days!.Value - earlier.Days!.Value < minimum)
            {
                throw S3Errors.InvalidArgument(
                    $"'Days' in the 'Transition' action for StorageClass '{later.StorageClass}' for filter '{filter}' must be "
                    + $"{minimum.ToString(CultureInfo.InvariantCulture)} days more than 'filter '{filter}'' in the 'Transition' action for StorageClass '{earlier.StorageClass}'");
            }
        }
    }
}

/// <summary>A bucket's lifecycle configuration.</summary>
internal sealed partial class S3Bucket
{
    /// <summary>The lifecycle rules, as sent; null when the bucket has no lifecycle configuration.</summary>
    public List<LifecycleRule>? LifecycleRules { get; set; }

    /// <summary>The <c>x-amz-transition-default-minimum-object-size</c> sent with the lifecycle configuration.</summary>
    public string? TransitionDefaultMinimumObjectSize { get; set; }
}

/// <summary>What a lifecycle rule stamps on a multipart upload.</summary>
internal sealed partial class S3MultipartUpload
{
    /// <summary>When an <c>AbortIncompleteMultipartUpload</c> rule would abort the upload; fixed at creation.</summary>
    public DateTime? AbortDate { get; set; }

    /// <summary>The id of the rule that sets <see cref="AbortDate"/>.</summary>
    public string? AbortRuleId { get; set; }
}
