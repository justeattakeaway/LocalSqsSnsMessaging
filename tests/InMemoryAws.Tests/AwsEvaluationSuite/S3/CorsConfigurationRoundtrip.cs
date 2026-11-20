// Translated from behavioral/s3/check_cors_configuration_roundtrip.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.cors-configuration-roundtrip</summary>
[Category("AwsEvaluationSuite")]
public sealed class CorsConfigurationRoundtrip
{
    /// <summary>
    /// PutBucketCors then GetBucketCors returns the same rules.
    /// </summary>
    [Test]
    [DisplayName("s3.cors-configuration-roundtrip")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var bucket = await S3Fidelity.CreateBucket(ctx, "sim2real-behav-bkt-");
        await s3.PutCORSConfigurationAsync(new PutCORSConfigurationRequest
        {
            BucketName = bucket,
            Configuration = new CORSConfiguration
            {
                Rules =
                [
                    new CORSRule
                    {
                        AllowedHeaders = ["Authorization", "Content-Type"],
                        AllowedMethods = ["GET", "PUT"],
                        AllowedOrigins = ["https://example.com"],
                        ExposeHeaders = ["x-amz-request-id"],
                        MaxAgeSeconds = 3600,
                    },
                ],
            },
        });
        var resp = await s3.GetCORSConfigurationAsync(new GetCORSConfigurationRequest { BucketName = bucket });
        var rules = resp.Configuration?.Rules ?? [];
        Ensure(rules.Count == 1, $"expected 1 CORS rule, got {Py.Repr(rules.Count)}");
        var rule = rules[0];
        Ensure(rule.AllowedMethods.ToHashSet().SetEquals(["GET", "PUT"]), $"AllowedMethods: {Py.Repr(rule.AllowedMethods)}");
        Ensure(rule.AllowedOrigins.SequenceEqual(["https://example.com"]), $"AllowedOrigins: {Py.Repr(rule.AllowedOrigins)}");
        Ensure(rule.MaxAgeSeconds == 3600, $"MaxAgeSeconds: {rule.MaxAgeSeconds}");
        Ensure(rule.AllowedHeaders.ToHashSet().SetEquals(["Authorization", "Content-Type"]), $"AllowedHeaders: {Py.Repr(rule.AllowedHeaders)}");
        Ensure(rule.ExposeHeaders.SequenceEqual(["x-amz-request-id"]), $"ExposeHeaders: {Py.Repr(rule.ExposeHeaders)}");
    }
}
