// Translated from behavioral/s3/check_bucket_metrics_configuration_delete_reflected_in_list_then_get_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.bucket-metrics-configuration-delete-reflected-in-list-then-get-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class BucketMetricsConfigurationDeleteReflectedInListThenGetRejected
{
    /// <summary>
    /// Deleting one of two metrics configurations removes it from ListBucketMetricsConfigurations,
    /// leaves the other intact, and a Get on the deleted Id fails NoSuchConfiguration.
    /// </summary>
    [Test]
    [DisplayName("s3.bucket-metrics-configuration-delete-reflected-in-list-then-get-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var bucket = await S3Fidelity.CreateBucket(ctx, "sim2real-behav-s3metrics-");

        const string idKeep = "cfg-keep";
        const string idDrop = "cfg-drop";

        await Put(s3, bucket, idKeep, "keep/");
        ctx.OnCleanup(() => s3.DeleteBucketMetricsConfigurationAsync(new DeleteBucketMetricsConfigurationRequest { BucketName = bucket, MetricsId = idKeep }));
        await Put(s3, bucket, idDrop, "drop/");

        var before = await s3.ListBucketMetricsConfigurationsAsync(new ListBucketMetricsConfigurationsRequest { BucketName = bucket });
        var beforeIds = (before.MetricsConfigurationList ?? []).Select(c => c.MetricsId).ToHashSet(StringComparer.Ordinal);
        Ensure(beforeIds.IsSupersetOf([idKeep, idDrop]), Py.Repr(beforeIds.ToList()));

        await s3.DeleteBucketMetricsConfigurationAsync(new DeleteBucketMetricsConfigurationRequest { BucketName = bucket, MetricsId = idDrop });

        var after = await s3.ListBucketMetricsConfigurationsAsync(new ListBucketMetricsConfigurationsRequest { BucketName = bucket });
        var afterIds = (after.MetricsConfigurationList ?? []).Select(c => c.MetricsId).ToHashSet(StringComparer.Ordinal);

        Ensure(!afterIds.Contains(idDrop), Py.Repr(afterIds.ToList()));
        Ensure(afterIds.Contains(idKeep), Py.Repr(afterIds.ToList()));

        var byId = (after.MetricsConfigurationList ?? []).ToDictionary(c => c.MetricsId, StringComparer.Ordinal);
        var keepFilter = S3Fidelity.Boto(byId[idKeep].MetricsFilter);
        Ensure(Equals(keepFilter?.GetValueOrDefault("Prefix"), "keep/"), Py.Repr(keepFilter));

        // The .NET SDK answers GetBucketMetricsConfiguration's 404 as an empty success; the refusal is read off the wire.
        var error = await S3Fidelity.RawError(ctx, "GET", $"{bucket}?metrics&id={idDrop}", "expected GetBucketMetricsConfiguration for a deleted Id to fail");
        Ensure(error.Code == "NoSuchConfiguration", error.ToString());
        Ensure(error.Message == "The specified configuration does not exist.", error.Message ?? "None");
    }

    private static Task Put(IAmazonS3 s3, string bucket, string id, string prefix) =>
        s3.PutBucketMetricsConfigurationAsync(new PutBucketMetricsConfigurationRequest
        {
            BucketName = bucket,
            MetricsId = id,
            MetricsConfiguration = new MetricsConfiguration { MetricsId = id, MetricsFilter = new MetricsFilter { MetricsFilterPredicate = new MetricsPrefixPredicate(prefix) } },
        });
}
