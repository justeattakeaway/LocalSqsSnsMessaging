// Translated from behavioral/s3/check_put_bucket_cors_over_100_rules_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit abd0811).
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.put-bucket-cors-over-100-rules-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class PutBucketCorsOver100RulesRejected
{
    /// <summary>
    /// PutBucketCors with 101 rules is rejected; the documented ceiling of 100 rules is
    /// accepted and reads back in full through GetBucketCors.
    /// </summary>
    [Test]
    [DisplayName("s3.put-bucket-cors-over-100-rules-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var bucket = await S3Fidelity.CreateBucket(ctx, "sim2real-corslimit-");

        var (code, message) = await S3Fidelity.ErrorPair(
            () => s3.PutCORSConfigurationAsync(new PutCORSConfigurationRequest
            {
                BucketName = bucket,
                Configuration = new CORSConfiguration { Rules = Rules(101) },
            }),
            "expected the call to be rejected, but it succeeded");
        Ensure(code == "InvalidRequest", $"expected InvalidRequest, got {Py.Repr(code)}: {Py.Repr(message)}");
        Ensure(message == "The number of CORS rules should not exceed allowed limit of 100 rules.", $"unexpected message {Py.Repr(message)}");

        // effect absent: the rejected PUT never set a CORS configuration on the bucket
        var (absentCode, absentMessage) = await S3Fidelity.ErrorPair(
            () => s3.GetCORSConfigurationAsync(new GetCORSConfigurationRequest { BucketName = bucket }),
            "expected the call to be rejected, but it succeeded");
        Ensure(absentCode == "NoSuchCORSConfiguration", $"expected NoSuchCORSConfiguration, got {Py.Repr(absentCode)}: {Py.Repr(absentMessage)}");

        // positive control: the documented ceiling of exactly 100 rules is accepted in full
        await s3.PutCORSConfigurationAsync(new PutCORSConfigurationRequest
        {
            BucketName = bucket,
            Configuration = new CORSConfiguration { Rules = Rules(100) },
        });
        var got = (await s3.GetCORSConfigurationAsync(new GetCORSConfigurationRequest { BucketName = bucket })).Configuration?.Rules ?? [];
        Ensure(got.Count == 100, $"expected 100 rules to read back, got {got.Count}");
        var origins = got.Select(rule => rule.AllowedOrigins[0]).ToHashSet(StringComparer.Ordinal);
        Ensure(
            origins.SetEquals(Enumerable.Range(0, 100).Select(i => $"https://rule{i}.example.com")),
            $"expected all 100 distinct origins to round-trip, got {Py.Repr(origins.Order(StringComparer.Ordinal).ToList())}");
    }

    /// <summary><c>_rules(count)</c>: one GET rule per distinct origin.</summary>
    private static List<CORSRule> Rules(int count) =>
        [.. Enumerable.Range(0, count).Select(i => new CORSRule { AllowedMethods = ["GET"], AllowedOrigins = [$"https://rule{i}.example.com"] })];
}
