// Translated from behavioral/s3/check_op_contract_put_bucket_cors.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.op-contract-putBucketCors</summary>
[Category("AwsEvaluationSuite")]
public sealed class OpContractPutBucketCors
{
    /// <summary>
    /// PutBucketCors persists the exact CORS rule set; errors NoSuchBucket for a missing bucket.
    /// </summary>
    [Test]
    [DisplayName("s3.op-contract-putBucketCors")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        (string? Code, string? Message) noSuchBucket = ("NoSuchBucket", "The specified bucket does not exist");
        var bucket = await S3Fidelity.CreateBucket(ctx, "sim2real-behav-");
        var s3 = ctx.S3;
        var corsConfig = new CORSConfiguration
        {
            Rules = [new CORSRule { AllowedMethods = ["GET", "PUT"], AllowedOrigins = ["https://example.com"], AllowedHeaders = ["*"], MaxAgeSeconds = 3000 }],
        };
        await s3.PutCORSConfigurationAsync(new PutCORSConfigurationRequest { BucketName = bucket, Configuration = corsConfig });
        ctx.OnCleanup(() => s3.DeleteCORSConfigurationAsync(new DeleteCORSConfigurationRequest { BucketName = bucket }));
        var resp = await s3.GetCORSConfigurationAsync(new GetCORSConfigurationRequest { BucketName = bucket });
        var rules = resp.Configuration.Rules;
        Ensure(rules.Count == 1, $"expected 1 CORS rule, got {Py.Repr(rules.Count)}");
        Ensure(rules[0].AllowedMethods.SequenceEqual(["GET", "PUT"]), "assert rules[0][\"AllowedMethods\"] == [\"GET\", \"PUT\"]");
        Ensure(rules[0].AllowedOrigins.SequenceEqual(["https://example.com"]), "assert rules[0][\"AllowedOrigins\"] == [\"https://example.com\"]");
        Ensure(rules[0].MaxAgeSeconds == 3000, "assert rules[0][\"MaxAgeSeconds\"] == 3000");

        // replacing the config fully overwrites the previous rule set (not a merge)
        await s3.PutCORSConfigurationAsync(new PutCORSConfigurationRequest
        {
            BucketName = bucket,
            Configuration = new CORSConfiguration { Rules = [new CORSRule { AllowedMethods = ["POST"], AllowedOrigins = ["*"] }] },
        });
        var resp2 = await s3.GetCORSConfigurationAsync(new GetCORSConfigurationRequest { BucketName = bucket });
        Ensure(resp2.Configuration.Rules.Count == 1, "assert len(resp2[\"CORSRules\"]) == 1");
        Ensure(resp2.Configuration.Rules[0].AllowedMethods.SequenceEqual(["POST"]), "assert resp2[\"CORSRules\"][0][\"AllowedMethods\"] == [\"POST\"]");

        // distinct error boundary: nonexistent bucket
        var missing = FidelityContext.UniqueName("sim2real-behav-missing-");
        var code = await S3Fidelity.Refusal(() => s3.PutCORSConfigurationAsync(new PutCORSConfigurationRequest { BucketName = missing, Configuration = corsConfig }));
        Ensure(code == noSuchBucket, $"expected NoSuchBucket, got {S3Fidelity.Repr(code)}");
    }
}
