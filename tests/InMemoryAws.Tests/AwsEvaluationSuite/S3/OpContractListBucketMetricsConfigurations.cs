// Translated from behavioral/s3/check_op_contract_list_bucket_metrics_configurations.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.op-contract-listBucketMetricsConfigurations</summary>
[Category("AwsEvaluationSuite")]
public sealed class OpContractListBucketMetricsConfigurations
{
    /// <summary>S3's refusal of a bucket that does not exist, as real S3 returns it.</summary>
    private static readonly (string, string) NoSuchBucket = ("NoSuchBucket", "The specified bucket does not exist");

    private const string BucketPrefix = "sim2real-behav-";

    /// <summary>
    /// ListBucketMetricsConfigurations enumerates configured metrics filters; errors NoSuchBucket when missing.
    /// </summary>
    [Test]
    [DisplayName("s3.op-contract-listBucketMetricsConfigurations")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var bucket = await S3Fidelity.CreateBucket(ctx, BucketPrefix);
        var s3 = ctx.S3;

        // no configurations yet
        var resp0 = await s3.ListBucketMetricsConfigurationsAsync(new ListBucketMetricsConfigurationsRequest { BucketName = bucket });
        Ensure((resp0.MetricsConfigurationList ?? []).Count == 0, $"expected empty list, got {Py.Repr((resp0.MetricsConfigurationList ?? []).Select(c => c.MetricsId).ToList())}");
        Ensure(resp0.IsTruncated == false, $"expected IsTruncated False, got {Py.Repr(resp0.IsTruncated)}");

        string[] ids = ["metrics-a", "metrics-b"];
        foreach (var mid in ids)
        {
            await s3.PutBucketMetricsConfigurationAsync(new PutBucketMetricsConfigurationRequest
            {
                BucketName = bucket,
                MetricsId = mid,
                MetricsConfiguration = new MetricsConfiguration { MetricsId = mid, MetricsFilter = new MetricsFilter { MetricsFilterPredicate = new MetricsPrefixPredicate($"{mid}/") } },
            });
            ctx.OnCleanup(() => s3.DeleteBucketMetricsConfigurationAsync(new DeleteBucketMetricsConfigurationRequest { BucketName = bucket, MetricsId = mid }));
        }

        var resp1 = await s3.ListBucketMetricsConfigurationsAsync(new ListBucketMetricsConfigurationsRequest { BucketName = bucket });
        var gotIds = resp1.MetricsConfigurationList.Select(c => c.MetricsId).Order(StringComparer.Ordinal).ToList();
        Ensure(gotIds.SequenceEqual(ids.Order(StringComparer.Ordinal)), $"expected {Py.Repr(ids.ToList())}, got {Py.Repr(gotIds)}");

        // distinct error boundary: nonexistent bucket
        var missing = FidelityContext.UniqueName(BucketPrefix + "missing-");
        var code = await S3Fidelity.ClientError(() => s3.ListBucketMetricsConfigurationsAsync(new ListBucketMetricsConfigurationsRequest { BucketName = missing }));
        Ensure(code == NoSuchBucket, $"expected NoSuchBucket, got {Py.Repr(code)}");
    }
}
