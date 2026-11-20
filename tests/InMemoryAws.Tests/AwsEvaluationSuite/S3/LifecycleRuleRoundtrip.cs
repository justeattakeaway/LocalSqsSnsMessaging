// Translated from behavioral/s3/check_lifecycle_rule_roundtrip.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.lifecycle-rule-roundtrip</summary>
[Category("AwsEvaluationSuite")]
public sealed class LifecycleRuleRoundtrip
{
    /// <summary>PutBucketLifecycleConfiguration then GetBucketLifecycleConfiguration round-trips.</summary>
    [Test]
    [DisplayName("s3.lifecycle-rule-roundtrip")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var bucket = await S3Fidelity.CreateBucket(ctx, "sim2real-behav-bkt-");
        await s3.PutLifecycleConfigurationAsync(new PutLifecycleConfigurationRequest
        {
            BucketName = bucket,
            Configuration = new LifecycleConfiguration
            {
                Rules =
                [
                    new LifecycleRule
                    {
                        Id = "sim2real-expire-30d",
                        Status = LifecycleRuleStatus.Enabled,
                        Filter = new LifecycleFilter { Prefix = "logs/" },
                        Expiration = new LifecycleRuleExpiration { Days = 30 },
                    },
                ],
            },
        });
        var resp = await s3.GetLifecycleConfigurationAsync(new GetLifecycleConfigurationRequest { BucketName = bucket });
        var rules = resp.Configuration?.Rules ?? [];
        Ensure(rules.Count == 1, $"expected exactly 1 rule, got {rules.Count}: {Py.Repr(rules.Select(S3Fidelity.Boto).ToList())}");
        Ensure(rules[0].Id == "sim2real-expire-30d", $"expected rule ID 'sim2real-expire-30d', got {Py.Repr(rules[0].Id)}");
        Ensure(rules[0].Status == "Enabled", $"expected Status 'Enabled', got {Py.Repr(rules[0].Status?.Value)}");
        Ensure(rules[0].Expiration?.Days == 30, $"expected Expiration.Days == 30, got {Py.Repr(S3Fidelity.Boto(rules[0]).GetValueOrDefault("Expiration"))}");
    }
}
