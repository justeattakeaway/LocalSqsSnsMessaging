// Translated from behavioral/s3/check_bucket_analytics_configuration_roundtrip.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.bucket-analytics-configuration-roundtrip</summary>
[Category("AwsEvaluationSuite")]
public sealed class BucketAnalyticsConfigurationRoundtrip
{
    /// <summary>
    /// Analytics configurations are addressable by Id, enumerable, and removable (then
    /// NoSuchConfiguration).
    /// </summary>
    [Test]
    [DisplayName("s3.bucket-analytics-configuration-roundtrip")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var bucket = await S3Fidelity.CreateBucket(ctx, "sim2real-behav-bkt-");
        const string aid = "whole-bucket-analytics";
        await s3.PutBucketAnalyticsConfigurationAsync(new PutBucketAnalyticsConfigurationRequest
        {
            BucketName = bucket,
            AnalyticsId = aid,
            AnalyticsConfiguration = new AnalyticsConfiguration { AnalyticsId = aid, StorageClassAnalysis = new StorageClassAnalysis() },
        });
        var got = (await s3.GetBucketAnalyticsConfigurationAsync(new GetBucketAnalyticsConfigurationRequest { BucketName = bucket, AnalyticsId = aid })).AnalyticsConfiguration;
        Ensure(got.AnalyticsId == aid, $"analytics config id mismatch: {Py.Repr(got.AnalyticsId)}");
        var listed = (await s3.ListBucketAnalyticsConfigurationsAsync(new ListBucketAnalyticsConfigurationsRequest { BucketName = bucket })).AnalyticsConfigurationList ?? [];
        Ensure(listed.Select(c => c.AnalyticsId).SequenceEqual([aid]), $"expected exactly ['{aid}'] listed, got {Py.Repr(listed.Select(c => c.AnalyticsId).ToList())}");
        await s3.DeleteBucketAnalyticsConfigurationAsync(new DeleteBucketAnalyticsConfigurationRequest { BucketName = bucket, AnalyticsId = aid });
        // The .NET SDK answers GetBucketAnalyticsConfiguration's 404 as an empty success; the refusal is
        // read off the wire.
        var error = await S3Fidelity.RawError(ctx, "GET", $"{bucket}?analytics&id={aid}");
        var (code, message) = (error.Code, error.Message);
        Ensure(code == "NoSuchConfiguration", $"expected NoSuchConfiguration, got {Py.Repr(code)}");
        const string expected = "The specified configuration does not exist.";
        Ensure(message == expected, $"expected message {Py.Repr(expected)}, got {Py.Repr(message)}");
        var listedAfter = (await s3.ListBucketAnalyticsConfigurationsAsync(new ListBucketAnalyticsConfigurationsRequest { BucketName = bucket })).AnalyticsConfigurationList ?? [];
        Ensure(listedAfter.Count == 0, $"expected no analytics configs listed after delete, got {Py.Repr(listedAfter.Select(c => c.AnalyticsId).ToList())}");
    }
}
