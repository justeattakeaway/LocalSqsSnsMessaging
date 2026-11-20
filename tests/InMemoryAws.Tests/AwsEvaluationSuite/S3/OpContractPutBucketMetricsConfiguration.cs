// Translated from behavioral/s3/check_op_contract_put_bucket_metrics_configuration.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.op-contract-putBucketMetricsConfiguration</summary>
[Category("AwsEvaluationSuite")]
public sealed class OpContractPutBucketMetricsConfiguration
{
    /// <summary>S3's refusal of a bucket that does not exist, as real S3 returns it.</summary>
    private static readonly (string, string) NoSuchBucket = ("NoSuchBucket", "The specified bucket does not exist");

    private const string BucketPrefix = "sim2real-behav-";

    /// <summary>
    /// PutBucketMetricsConfiguration stores the filter under Id; errors NoSuchBucket for a missing bucket.
    /// </summary>
    [Test]
    [DisplayName("s3.op-contract-putBucketMetricsConfiguration")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var bucket = await S3Fidelity.CreateBucket(ctx, BucketPrefix);
        var s3 = ctx.S3;
        const string metricsId = "prod-only";

        await s3.PutBucketMetricsConfigurationAsync(new PutBucketMetricsConfigurationRequest
        {
            BucketName = bucket,
            MetricsId = metricsId,
            MetricsConfiguration = new MetricsConfiguration
            {
                MetricsId = metricsId,
                // combining a Prefix with Tags requires the "And" wrapper (MetricsAndOperator);
                // the top-level Filter only accepts one of Prefix / Tag / And.
                MetricsFilter = new MetricsFilter
                {
                    MetricsFilterPredicate = new MetricsAndOperator([new MetricsPrefixPredicate("prod/"), new MetricsTagPredicate(new Tag { Key = "env", Value = "prod" })]),
                },
            },
        });
        ctx.OnCleanup(() => s3.DeleteBucketMetricsConfigurationAsync(new DeleteBucketMetricsConfigurationRequest { BucketName = bucket, MetricsId = metricsId }));

        var resp = await s3.GetBucketMetricsConfigurationAsync(new GetBucketMetricsConfigurationRequest { BucketName = bucket, MetricsId = metricsId });
        var cfg = resp.MetricsConfiguration;
        Ensure(cfg.MetricsId == metricsId, $"expected Id {Py.Repr(metricsId)}, got {Py.Repr(cfg.MetricsId)}");
        var and = (Dictionary<string, object?>)S3Fidelity.Boto(cfg.MetricsFilter)!["And"]!;
        var gotPrefix = and["Prefix"];
        Ensure(Equals(gotPrefix, "prod/"), $"expected And.Prefix 'prod/', got {Py.Repr(gotPrefix)}");
        var filterTags = and["Tags"];
        Ensure(
            Py.Equal(filterTags, new List<object?> { new Dictionary<string, object?> { ["Key"] = "env", ["Value"] = "prod" } }),
            $"unexpected filter tags: {Py.Repr(filterTags)}");

        // replacing the same Id overwrites the filter in place
        await s3.PutBucketMetricsConfigurationAsync(new PutBucketMetricsConfigurationRequest
        {
            BucketName = bucket,
            MetricsId = metricsId,
            MetricsConfiguration = new MetricsConfiguration { MetricsId = metricsId, MetricsFilter = new MetricsFilter { MetricsFilterPredicate = new MetricsPrefixPredicate("staging/") } },
        });
        var resp2 = await s3.GetBucketMetricsConfigurationAsync(new GetBucketMetricsConfigurationRequest { BucketName = bucket, MetricsId = metricsId });
        var gotPrefix2 = S3Fidelity.Boto(resp2.MetricsConfiguration.MetricsFilter)?.GetValueOrDefault("Prefix");
        Ensure(Equals(gotPrefix2, "staging/"), $"expected overwritten Filter.Prefix 'staging/', got {Py.Repr(gotPrefix2)}");

        // distinct error boundary: nonexistent bucket
        var missing = FidelityContext.UniqueName(BucketPrefix + "missing-");
        var code = await S3Fidelity.ClientError(() => s3.PutBucketMetricsConfigurationAsync(new PutBucketMetricsConfigurationRequest
        {
            BucketName = missing,
            MetricsId = metricsId,
            MetricsConfiguration = new MetricsConfiguration { MetricsId = metricsId },
        }));
        Ensure(code == NoSuchBucket, $"expected NoSuchBucket, got {Py.Repr(code)}");
    }
}
