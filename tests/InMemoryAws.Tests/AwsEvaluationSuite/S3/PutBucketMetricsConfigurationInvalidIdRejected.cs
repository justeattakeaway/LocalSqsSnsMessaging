// Translated from behavioral/s3/check_put_bucket_metrics_configuration_invalid_id_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.put-bucket-metrics-configuration-invalid-id-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class PutBucketMetricsConfigurationInvalidIdRejected
{
    /// <summary>A metrics configuration ID over the documented 64-character limit is rejected as invalid.</summary>
    [Test]
    [DisplayName("s3.put-bucket-metrics-configuration-invalid-id-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var bucket = await S3Fidelity.CreateBucket(ctx, "sim2real-metrics-");

        // Per the PutBucketMetricsConfiguration docs: "The ID has a 64 character limit and can
        // only contain letters, numbers, periods, dashes, and underscores."
        var tooLongId = new string('x', 65);
        var code = await ExpectErrorCode(() => s3.PutBucketMetricsConfigurationAsync(new PutBucketMetricsConfigurationRequest
        {
            BucketName = bucket,
            MetricsId = tooLongId,
            MetricsConfiguration = new MetricsConfiguration { MetricsId = tooLongId },
        }));
        Ensure(
            code == ("MalformedXML", "The XML you provided was not well-formed or did not validate against our published schema"),
            $"expected MalformedXML, got {Py.Repr(code)}");

        // effect absent: no configuration was ever created under that (invalid) id
        // The .NET SDK answers GetBucketMetricsConfiguration's 404 as an empty success; the refusal is read off the wire.
        var getCode = S3Fidelity.Botocore(await S3Fidelity.RawError(ctx, "GET", $"{bucket}?metrics&id={tooLongId}", "expected the call to be rejected, but it succeeded"));
        Ensure(
            getCode == ("NoSuchConfiguration", "The specified configuration does not exist."),
            $"expected NoSuchConfiguration, got {Py.Repr(getCode)}");

        // positive control: a valid, within-limits id succeeds
        const string validId = "ValidMetricsId123";
        await s3.PutBucketMetricsConfigurationAsync(new PutBucketMetricsConfigurationRequest
        {
            BucketName = bucket,
            MetricsId = validId,
            MetricsConfiguration = new MetricsConfiguration { MetricsId = validId },
        });
        ctx.OnCleanup(() => s3.DeleteBucketMetricsConfigurationAsync(new DeleteBucketMetricsConfigurationRequest { BucketName = bucket, MetricsId = validId }));
        var got = await s3.GetBucketMetricsConfigurationAsync(new GetBucketMetricsConfigurationRequest { BucketName = bucket, MetricsId = validId });
        Ensure(got.MetricsConfiguration.MetricsId == validId, $"expected {Py.Repr(validId)}, got {Py.Repr(got.MetricsConfiguration.MetricsId)}");
    }

    /// <summary>
    /// Call fn(); return its botocore (code, message). An unexpected SUCCESS raises, because an
    /// emulator ACCEPTING input real AWS refuses is the strongest signal available.
    /// </summary>
    private static Task<(string? Code, string? Message)> ExpectErrorCode(Func<Task> fn) =>
        S3Fidelity.ClientError(fn, "expected the call to be rejected, but it succeeded");
}
