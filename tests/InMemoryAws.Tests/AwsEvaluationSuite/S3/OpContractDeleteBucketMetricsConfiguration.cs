// Translated from behavioral/s3/check_op_contract_delete_bucket_metrics_configuration.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.op-contract-delete-bucket-metrics-configuration</summary>
[Category("AwsEvaluationSuite")]
public sealed class OpContractDeleteBucketMetricsConfiguration
{
    /// <summary>
    /// DeleteBucketMetricsConfiguration removes a named metrics filter; a missing bucket is rejected.
    /// </summary>
    [Test]
    [DisplayName("s3.op-contract-delete-bucket-metrics-configuration")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var bucket = await S3Fidelity.CreateBucket(ctx, "sim2real-behav-");
        const string metricsId = "sim2real-metrics";

        // --- success path: put a metrics config, delete it, confirm it's gone ---
        await s3.PutBucketMetricsConfigurationAsync(new PutBucketMetricsConfigurationRequest
        {
            BucketName = bucket,
            MetricsId = metricsId,
            MetricsConfiguration = new MetricsConfiguration
            {
                MetricsId = metricsId,
                MetricsFilter = new MetricsFilter { MetricsFilterPredicate = new MetricsPrefixPredicate("reports/") },
            },
        });
        var got = await s3.GetBucketMetricsConfigurationAsync(new GetBucketMetricsConfigurationRequest { BucketName = bucket, MetricsId = metricsId });
        var gotId = got.MetricsConfiguration.MetricsId;
        Ensure(gotId == metricsId, $"expected Id {Py.Repr(metricsId)}, got {Py.Repr(gotId)}");

        await s3.DeleteBucketMetricsConfigurationAsync(new DeleteBucketMetricsConfigurationRequest { BucketName = bucket, MetricsId = metricsId });
        // The .NET SDK answers GetBucketMetricsConfiguration's 404 as an empty success; the refusal is read off the wire.
        var (code, message) = S3Fidelity.Botocore(await S3Fidelity.RawError(ctx, "GET", $"{bucket}?metrics&id={metricsId}", "expected a ClientError, but the call SUCCEEDED"));
        Ensure(code == "NoSuchConfiguration", $"expected NoSuchConfiguration, got {Py.Repr(code)}");
        Ensure(message == "The specified configuration does not exist.", $"unexpected message {Py.Repr(message)}");

        // --- delete is NOT idempotent: deleting an already-absent Id is rejected too ---
        (code, message) = await ClientError(() => s3.DeleteBucketMetricsConfigurationAsync(new DeleteBucketMetricsConfigurationRequest { BucketName = bucket, MetricsId = metricsId }));
        Ensure(code == "NoSuchConfiguration", $"expected NoSuchConfiguration, got {Py.Repr(code)}");
        Ensure(message == "The specified configuration does not exist.", $"unexpected message {Py.Repr(message)}");

        // --- error boundary: operating against a bucket that does not exist ---
        var ghostBucket = FidelityContext.UniqueName("sim2real-behav-ghost-");
        (code, message) = await ClientError(() => s3.DeleteBucketMetricsConfigurationAsync(new DeleteBucketMetricsConfigurationRequest { BucketName = ghostBucket, MetricsId = metricsId }));
        Ensure(code == "NoSuchBucket", $"expected NoSuchBucket, got {Py.Repr(code)}");
        Ensure(message == "The specified bucket does not exist", $"unexpected message {Py.Repr(message)}");
    }

    /// <summary>
    /// The (Error.Code, Error.Message) of fn(); a code-less HEAD error yields the HTTP status. Raises
    /// AssertionError if fn succeeds.
    /// </summary>
    private static Task<(string? Code, string? Message)> ClientError(Func<Task> fn) =>
        S3Fidelity.ClientError(fn, "expected a ClientError, but the call SUCCEEDED");
}
