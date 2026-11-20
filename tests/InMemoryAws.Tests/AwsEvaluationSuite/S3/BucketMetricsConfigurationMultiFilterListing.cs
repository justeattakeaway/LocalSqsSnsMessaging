// Translated from behavioral/s3/check_bucket_metrics_configuration_multi_filter_listing.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.bucket-metrics-configuration-multi-filter-listing</summary>
[Category("AwsEvaluationSuite")]
public sealed class BucketMetricsConfigurationMultiFilterListing
{
    /// <summary>
    /// Multiple metrics configurations with distinct filter predicates are all
    /// returned by ListBucketMetricsConfigurations with correct filter fields and
    /// no duplication.
    /// </summary>
    [Test]
    [DisplayName("s3.bucket-metrics-configuration-multi-filter-listing")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var bucket = await S3Fidelity.CreateBucket(ctx, "sim2real-behav-s3metrics-");
        await s3.PutBucketTaggingAsync(new PutBucketTaggingRequest { BucketName = bucket, TagSet = [new Tag { Key = "sim2real", Value = "behavioral" }] });

        const string idPrefix = "cfg-prefix";
        const string idTag = "cfg-tag";
        const string idNone = "cfg-nofilter";

        await Put(s3, bucket, idPrefix, new MetricsFilter { MetricsFilterPredicate = new MetricsPrefixPredicate("logs/") });
        ctx.OnCleanup(() => Delete(s3, bucket, idPrefix));
        await Put(s3, bucket, idTag, new MetricsFilter { MetricsFilterPredicate = new MetricsTagPredicate(new Tag { Key = "project", Value = "alpha" }) });
        ctx.OnCleanup(() => Delete(s3, bucket, idTag));
        await Put(s3, bucket, idNone, null);
        ctx.OnCleanup(() => Delete(s3, bucket, idNone));

        var listing = await s3.ListBucketMetricsConfigurationsAsync(new ListBucketMetricsConfigurationsRequest { BucketName = bucket });
        var configs = listing.MetricsConfigurationList ?? [];
        var ids = configs.Select(c => c.MetricsId).ToList();

        // no duplication: each id appears exactly once
        Ensure(ids.Count(i => i == idPrefix) == 1, Py.Repr(ids));
        Ensure(ids.Count(i => i == idTag) == 1, Py.Repr(ids));
        Ensure(ids.Count(i => i == idNone) == 1, Py.Repr(ids));
        Ensure(configs.Count == 3, Py.Repr(ids));

        var byId = configs.ToDictionary(c => c.MetricsId, StringComparer.Ordinal);

        var prefixFilter = S3Fidelity.Boto(byId[idPrefix].MetricsFilter) ?? [];
        Ensure(Equals(prefixFilter.GetValueOrDefault("Prefix"), "logs/"), Py.Repr(prefixFilter));
        Ensure(
            !prefixFilter.ContainsKey("Tag") && !prefixFilter.ContainsKey("And"),
            $"prefix-only filter must not gain a Tag/And predicate: {Py.Repr(prefixFilter)}");

        var tagFilter = S3Fidelity.Boto(byId[idTag].MetricsFilter) ?? [];
        Ensure(
            Py.Equal(tagFilter.GetValueOrDefault("Tag"), new Dictionary<string, object?> { ["Key"] = "project", ["Value"] = "alpha" }),
            Py.Repr(tagFilter));
        Ensure(
            !tagFilter.ContainsKey("Prefix") && !tagFilter.ContainsKey("And"),
            $"tag-only filter must not gain a Prefix/And predicate: {Py.Repr(tagFilter)}");

        // a filter-less config either omits Filter entirely or omits its predicates
        if (S3Fidelity.Boto(byId[idNone].MetricsFilter) is { } f)
        {
            Ensure(
                string.IsNullOrEmpty(f.GetValueOrDefault("Prefix") as string) && f.GetValueOrDefault("Tag") is null && f.GetValueOrDefault("And") is null,
                Py.Repr(f));
        }
    }

    private static Task Put(IAmazonS3 s3, string bucket, string id, MetricsFilter? filter) =>
        s3.PutBucketMetricsConfigurationAsync(new PutBucketMetricsConfigurationRequest
        {
            BucketName = bucket,
            MetricsId = id,
            MetricsConfiguration = new MetricsConfiguration { MetricsId = id, MetricsFilter = filter },
        });

    private static Task Delete(IAmazonS3 s3, string bucket, string id) =>
        s3.DeleteBucketMetricsConfigurationAsync(new DeleteBucketMetricsConfigurationRequest { BucketName = bucket, MetricsId = id });
}
