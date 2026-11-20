// Translated from behavioral/s3/check_bucket_analytics_configuration_lifecycle.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.bucket-analytics-configuration-lifecycle</summary>
[Category("AwsEvaluationSuite")]
public sealed class BucketAnalyticsConfigurationLifecycle
{
    private const string NoSuchConfigurationMessage = "The specified configuration does not exist.";

    /// <summary>
    /// PutBucketAnalyticsConfiguration creates a named config visible in both
    /// ListBucketAnalyticsConfigurations and GetBucketAnalyticsConfiguration;
    /// DeleteBucketAnalyticsConfiguration removes it from both.
    /// </summary>
    [Test]
    [DisplayName("s3.bucket-analytics-configuration-lifecycle")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var name = await S3Fidelity.CreateBucket(ctx, "sim2real-behav-analytics-");
        const string configId = "config-1";

        await s3.PutBucketAnalyticsConfigurationAsync(new PutBucketAnalyticsConfigurationRequest
        {
            BucketName = name,
            AnalyticsId = configId,
            AnalyticsConfiguration = new AnalyticsConfiguration { AnalyticsId = configId, StorageClassAnalysis = new StorageClassAnalysis() },
        });

        var got = await s3.GetBucketAnalyticsConfigurationAsync(new GetBucketAnalyticsConfigurationRequest { BucketName = name, AnalyticsId = configId });
        Ensure(got.AnalyticsConfiguration?.AnalyticsId == configId, Py.Repr(got.AnalyticsConfiguration?.AnalyticsId));

        var listed = await s3.ListBucketAnalyticsConfigurationsAsync(new ListBucketAnalyticsConfigurationsRequest { BucketName = name });
        var ids = (listed.AnalyticsConfigurationList ?? []).Select(c => c.AnalyticsId).ToList();
        Ensure(ids.SequenceEqual([configId]), $"expected exactly [{Py.Repr(configId)}] listed on a fresh bucket, got {Py.Repr(ids)}");

        await s3.DeleteBucketAnalyticsConfigurationAsync(new DeleteBucketAnalyticsConfigurationRequest { BucketName = name, AnalyticsId = configId });

        // The .NET SDK answers GetBucketAnalyticsConfiguration's 404 as an empty success; the refusal is read off the wire.
        var (code, message) = S3Fidelity.Botocore(await S3Fidelity.RawError(ctx, "GET", $"{name}?analytics&id={configId}", "expected the call to be rejected, but it SUCCEEDED"));
        Ensure(code == "NoSuchConfiguration", code ?? "None");
        Ensure(message == NoSuchConfigurationMessage, $"unexpected message {Py.Repr(message)}");

        var listed2 = await s3.ListBucketAnalyticsConfigurationsAsync(new ListBucketAnalyticsConfigurationsRequest { BucketName = name });
        var ids2 = (listed2.AnalyticsConfigurationList ?? []).Select(c => c.AnalyticsId).ToList();
        Ensure(ids2.Count == 0, $"expected no analytics configurations after delete, got {Py.Repr(ids2)}");

        // A second delete on the now-dead id must also fail, not silently no-op: it proves the
        // id is genuinely gone rather than merely absent from the last read.
        var (redeleteCode, redeleteMessage) = await S3Fidelity.ClientError(
            () => s3.DeleteBucketAnalyticsConfigurationAsync(new DeleteBucketAnalyticsConfigurationRequest { BucketName = name, AnalyticsId = configId }),
            "expected the call to be rejected, but it SUCCEEDED");
        Ensure(redeleteCode == "NoSuchConfiguration", redeleteCode ?? "None");
        Ensure(redeleteMessage == NoSuchConfigurationMessage, $"unexpected message {Py.Repr(redeleteMessage)}");
    }
}
