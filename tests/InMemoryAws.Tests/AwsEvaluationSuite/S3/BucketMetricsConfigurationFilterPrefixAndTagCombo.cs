// Translated from behavioral/s3/check_bucket_metrics_configuration_filter_prefix_and_tag_combo.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.bucket-metrics-configuration-filter-prefix-and-tag-combo</summary>
[Category("AwsEvaluationSuite")]
public sealed class BucketMetricsConfigurationFilterPrefixAndTagCombo
{
    /// <summary>
    /// A metrics filter combining a prefix and tags under And reads back under And with both
    /// predicates intact, not flattened onto top-level Prefix/Tag predicates.
    /// </summary>
    [Test]
    [DisplayName("s3.bucket-metrics-configuration-filter-prefix-and-tag-combo")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var bucket = await S3Fidelity.CreateBucket(ctx, "sim2real-behav-s3metrics-");

        const string configId = "cfg-and-combo";
        List<Tag> tags = [new Tag { Key = "project", Value = "alpha" }, new Tag { Key = "env", Value = "prod" }];
        await s3.PutBucketMetricsConfigurationAsync(new PutBucketMetricsConfigurationRequest
        {
            BucketName = bucket,
            MetricsId = configId,
            MetricsConfiguration = new MetricsConfiguration
            {
                MetricsId = configId,
                MetricsFilter = new MetricsFilter
                {
                    MetricsFilterPredicate = new MetricsAndOperator([new MetricsPrefixPredicate("combo/"), .. tags.Select(t => new MetricsTagPredicate(t))]),
                },
            },
        });
        ctx.OnCleanup(() => s3.DeleteBucketMetricsConfigurationAsync(new DeleteBucketMetricsConfigurationRequest { BucketName = bucket, MetricsId = configId }));

        var got = await s3.GetBucketMetricsConfigurationAsync(new GetBucketMetricsConfigurationRequest { BucketName = bucket, MetricsId = configId });
        var metricsCfg = got.MetricsConfiguration;
        Ensure(metricsCfg.MetricsId == configId, Py.Repr(metricsCfg.MetricsId));

        var filt = S3Fidelity.Boto(metricsCfg.MetricsFilter) ?? [];
        // AND semantics must be preserved under the And conjunction, not
        // flattened onto top-level Prefix/Tag predicates.
        Ensure(!filt.ContainsKey("Prefix"), Py.Repr(filt));
        Ensure(!filt.ContainsKey("Tag"), Py.Repr(filt));
        Ensure(filt.ContainsKey("And"), Py.Repr(filt));

        var andOp = (Dictionary<string, object?>)filt["And"]!;
        Ensure(Equals(andOp.GetValueOrDefault("Prefix"), "combo/"), Py.Repr(andOp));
        var gotTags = ((andOp.GetValueOrDefault("Tags") as List<object?>) ?? []).Cast<Dictionary<string, object?>>().ToList();
        Ensure(
            gotTags.Select(t => ((string?)t["Key"], (string?)t["Value"])).ToHashSet().SetEquals(tags.Select(t => ((string?)t.Key, (string?)t.Value))),
            Py.Repr(gotTags));
        Ensure(gotTags.Count == tags.Count, Py.Repr(gotTags));
    }
}
