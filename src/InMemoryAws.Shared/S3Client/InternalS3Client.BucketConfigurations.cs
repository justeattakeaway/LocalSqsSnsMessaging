using System.Net;
using InMemoryAws.S3.Model;

namespace InMemoryAws.S3;

/// <summary>
/// A bucket's named configurations: metrics, analytics and S3 Intelligent-Tiering (inventory is in
/// <c>InternalS3Client.Inventory.cs</c>). Each kind is a set keyed by id, kept as sent once it
/// passes S3's checks and answered back; the cloud publishes no metrics, runs no analysis and
/// moves no objects between tiers.
/// </summary>
internal sealed partial class InternalS3Client
{
    /// <summary>How many configurations of each kind a bucket may hold.</summary>
    private const int MaxBucketConfigurations = 1000;

    /// <summary>How many configurations one List call answers.</summary>
    private const int BucketConfigurationPageSize = 100;

    public override Task<PutBucketMetricsConfigurationOutput> PutBucketMetricsConfigurationAsync(PutBucketMetricsConfigurationRequest request, CancellationToken cancellationToken)
    {
        var configuration = request.MetricsConfiguration ?? throw S3Errors.MalformedXml();
        CheckConfigurationIds(request.Id, configuration.Id);
        if (configuration.Filter is { } filter
            && (filter.Prefix is null ? 0 : 1) + (filter.Tag is null ? 0 : 1) + (filter.AccessPointArn is null ? 0 : 1) + (filter.And is null ? 0 : 1) > 1)
        {
            throw S3Errors.MalformedXml();
        }

        PutConfiguration(request.Bucket, b => b.MetricsConfigurations, request.Id!, configuration);
        return Task.FromResult(new PutBucketMetricsConfigurationOutput());
    }

    public override Task<GetBucketMetricsConfigurationOutput> GetBucketMetricsConfigurationAsync(GetBucketMetricsConfigurationRequest request, CancellationToken cancellationToken) =>
        Task.FromResult(new GetBucketMetricsConfigurationOutput { MetricsConfiguration = GetConfiguration(request.Bucket, b => b.MetricsConfigurations, request.Id) });

    public override Task<DeleteBucketMetricsConfigurationOutput> DeleteBucketMetricsConfigurationAsync(DeleteBucketMetricsConfigurationRequest request, CancellationToken cancellationToken)
    {
        DeleteConfiguration(request.Bucket, b => b.MetricsConfigurations, request.Id);
        return Task.FromResult(new DeleteBucketMetricsConfigurationOutput());
    }

    public override Task<ListBucketMetricsConfigurationsOutput> ListBucketMetricsConfigurationsAsync(ListBucketMetricsConfigurationsRequest request, CancellationToken cancellationToken)
    {
        var (page, truncated, next) = ListConfigurations(request.Bucket, b => b.MetricsConfigurations, request.ContinuationToken);
        return Task.FromResult(new ListBucketMetricsConfigurationsOutput
        {
            IsTruncated = truncated,
            ContinuationToken = request.ContinuationToken,
            NextContinuationToken = next,
            MetricsConfigurationList = page,
        });
    }

    public override Task<PutBucketAnalyticsConfigurationOutput> PutBucketAnalyticsConfigurationAsync(PutBucketAnalyticsConfigurationRequest request, CancellationToken cancellationToken)
    {
        var configuration = request.AnalyticsConfiguration ?? throw S3Errors.MalformedXml();
        CheckConfigurationIds(request.Id, configuration.Id);
        if (configuration.StorageClassAnalysis is null
            || (configuration.Filter is { } filter && (filter.Prefix is null ? 0 : 1) + (filter.Tag is null ? 0 : 1) + (filter.And is null ? 0 : 1) > 1))
        {
            throw S3Errors.MalformedXml();
        }

        PutConfiguration(request.Bucket, b => b.AnalyticsConfigurations, request.Id!, configuration);
        return Task.FromResult(new PutBucketAnalyticsConfigurationOutput());
    }

    public override Task<GetBucketAnalyticsConfigurationOutput> GetBucketAnalyticsConfigurationAsync(GetBucketAnalyticsConfigurationRequest request, CancellationToken cancellationToken) =>
        Task.FromResult(new GetBucketAnalyticsConfigurationOutput { AnalyticsConfiguration = GetConfiguration(request.Bucket, b => b.AnalyticsConfigurations, request.Id) });

    public override Task<DeleteBucketAnalyticsConfigurationOutput> DeleteBucketAnalyticsConfigurationAsync(DeleteBucketAnalyticsConfigurationRequest request, CancellationToken cancellationToken)
    {
        DeleteConfiguration(request.Bucket, b => b.AnalyticsConfigurations, request.Id);
        return Task.FromResult(new DeleteBucketAnalyticsConfigurationOutput());
    }

    public override Task<ListBucketAnalyticsConfigurationsOutput> ListBucketAnalyticsConfigurationsAsync(ListBucketAnalyticsConfigurationsRequest request, CancellationToken cancellationToken)
    {
        var (page, truncated, next) = ListConfigurations(request.Bucket, b => b.AnalyticsConfigurations, request.ContinuationToken);
        return Task.FromResult(new ListBucketAnalyticsConfigurationsOutput
        {
            IsTruncated = truncated,
            ContinuationToken = request.ContinuationToken,
            NextContinuationToken = next,
            AnalyticsConfigurationList = page,
        });
    }

    /// <summary>
    /// Stores an S3 Intelligent-Tiering configuration: enabled or disabled, with one or two tierings,
    /// each to a distinct archive tier after its days (90 to 730 for <c>ARCHIVE_ACCESS</c>, 180 to
    /// 730 for <c>DEEP_ARCHIVE_ACCESS</c>).
    /// </summary>
    public override Task<PutBucketIntelligentTieringConfigurationOutput> PutBucketIntelligentTieringConfigurationAsync(PutBucketIntelligentTieringConfigurationRequest request, CancellationToken cancellationToken)
    {
        var configuration = request.IntelligentTieringConfiguration ?? throw S3Errors.MalformedXml();
        CheckConfigurationIds(request.Id, configuration.Id);
        if (configuration.Status is not ("Enabled" or "Disabled") || configuration.Tierings is not { Count: > 0 and <= 2 } tierings)
        {
            throw S3Errors.MalformedXml();
        }

        var tiers = new HashSet<string>(StringComparer.Ordinal);
        foreach (var tiering in tierings)
        {
            var (low, high) = tiering.AccessTier switch
            {
                "ARCHIVE_ACCESS" => (90, 730),
                "DEEP_ARCHIVE_ACCESS" => (180, 730),
                _ => throw S3Errors.MalformedXml(),
            };
            if (!tiers.Add(tiering.AccessTier!))
            {
                throw S3Errors.InvalidArgument("The Intelligent-Tiering configuration contains a duplicate access tier.");
            }

            if (tiering.Days is not { } days || days < low)
            {
                throw new S3Exception("InvalidAccessTier", $"Days specified in {tiering.AccessTier} tier should not be less than {low}.", HttpStatusCode.BadRequest);
            }

            if (days > high)
            {
                throw new S3Exception("InvalidAccessTier", $"Days specified in {tiering.AccessTier} tier should not be more than {high}.", HttpStatusCode.BadRequest);
            }
        }

        PutConfiguration(request.Bucket, b => b.IntelligentTieringConfigurations, request.Id!, configuration);
        return Task.FromResult(new PutBucketIntelligentTieringConfigurationOutput());
    }

    public override Task<GetBucketIntelligentTieringConfigurationOutput> GetBucketIntelligentTieringConfigurationAsync(GetBucketIntelligentTieringConfigurationRequest request, CancellationToken cancellationToken) =>
        Task.FromResult(new GetBucketIntelligentTieringConfigurationOutput
        {
            IntelligentTieringConfiguration = GetConfiguration(request.Bucket, b => b.IntelligentTieringConfigurations, request.Id),
        });

    public override Task<DeleteBucketIntelligentTieringConfigurationOutput> DeleteBucketIntelligentTieringConfigurationAsync(DeleteBucketIntelligentTieringConfigurationRequest request, CancellationToken cancellationToken)
    {
        DeleteConfiguration(request.Bucket, b => b.IntelligentTieringConfigurations, request.Id);
        return Task.FromResult(new DeleteBucketIntelligentTieringConfigurationOutput());
    }

    public override Task<ListBucketIntelligentTieringConfigurationsOutput> ListBucketIntelligentTieringConfigurationsAsync(ListBucketIntelligentTieringConfigurationsRequest request, CancellationToken cancellationToken)
    {
        var (page, truncated, next) = ListConfigurations(request.Bucket, b => b.IntelligentTieringConfigurations, request.ContinuationToken);
        return Task.FromResult(new ListBucketIntelligentTieringConfigurationsOutput
        {
            IsTruncated = truncated,
            ContinuationToken = request.ContinuationToken,
            NextContinuationToken = next,
            IntelligentTieringConfigurationList = page,
        });
    }

    /// <summary>
    /// A configuration's id, in the query and in the body, as S3's schema has it: 1 to 64 letters,
    /// digits, periods, dashes and underscores, the same in both places.
    /// </summary>
    private static void CheckConfigurationIds(string? queryId, string? bodyId)
    {
        if (!IsConfigurationId(queryId) || !IsConfigurationId(bodyId))
        {
            throw S3Errors.MalformedXml();
        }

        if (!string.Equals(queryId, bodyId, StringComparison.Ordinal))
        {
            throw S3Errors.InvalidArgument("Configuration ID in the request query string does not match the ID in the request body.", "id", queryId);
        }
    }

    private static bool IsConfigurationId(string? id) =>
        id is { Length: > 0 and <= 64 } && id.All(c => c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '.' or '-' or '_');

    private void PutConfiguration<T>(string? bucketName, Func<S3Bucket, SortedDictionary<string, T>> set, string id, T configuration)
    {
        lock (Store.Gate)
        {
            var configurations = set(GetBucket(bucketName));
            if (!configurations.ContainsKey(id) && configurations.Count >= MaxBucketConfigurations)
            {
                throw new S3Exception(
                    "TooManyConfigurations",
                    "You are attempting to create a new configuration but have already reached the 1,000-configuration limit.",
                    HttpStatusCode.BadRequest);
            }

            configurations[id] = configuration;
        }
    }

    private T GetConfiguration<T>(string? bucketName, Func<S3Bucket, SortedDictionary<string, T>> set, string? id)
    {
        lock (Store.Gate)
        {
            var bucket = GetBucket(bucketName);
            return id is not null && set(bucket).TryGetValue(id, out var configuration) ? configuration : throw NoSuchBucketConfiguration(bucket);
        }
    }

    private void DeleteConfiguration<T>(string? bucketName, Func<S3Bucket, SortedDictionary<string, T>> set, string? id)
    {
        lock (Store.Gate)
        {
            var bucket = GetBucket(bucketName);
            if (id is null || !set(bucket).Remove(id))
            {
                throw NoSuchBucketConfiguration(bucket);
            }
        }
    }

    /// <summary>
    /// A page of a bucket's configurations of one kind, in id order, from the one after the
    /// continuation token's id; with whether more follow, and the token for them.
    /// </summary>
    private (List<T>? Page, bool Truncated, string? Next) ListConfigurations<T>(
        string? bucketName, Func<S3Bucket, SortedDictionary<string, T>> set, string? continuationToken)
    {
        lock (Store.Gate)
        {
            var bucket = GetBucket(bucketName);
            var after = DecodeToken(continuationToken);
            var remaining = set(bucket).Where(c => after is null || string.CompareOrdinal(c.Key, after) > 0).ToList();
            var page = remaining.Take(BucketConfigurationPageSize).ToList();
            var truncated = remaining.Count > page.Count;
            return (page.Count == 0 ? null : [.. page.Select(c => c.Value)], truncated, truncated ? EncodeToken(page[^1].Key) : null);
        }
    }

    private static S3Exception NoSuchBucketConfiguration(S3Bucket bucket) =>
        S3Errors.NoSuchConfiguration("NoSuchConfiguration", "The specified configuration does not exist.", bucket.Name);
}

/// <summary>A bucket's named configurations, each kind by id in ordinal order.</summary>
internal sealed partial class S3Bucket
{
    public SortedDictionary<string, MetricsConfiguration> MetricsConfigurations { get; } = new(StringComparer.Ordinal);

    public SortedDictionary<string, AnalyticsConfiguration> AnalyticsConfigurations { get; } = new(StringComparer.Ordinal);

    public SortedDictionary<string, IntelligentTieringConfiguration> IntelligentTieringConfigurations { get; } = new(StringComparer.Ordinal);
}
