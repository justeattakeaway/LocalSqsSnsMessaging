// Translated from behavioral/s3/check_object_lock_configuration_roundtrip.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.object-lock-configuration-roundtrip</summary>
[Category("AwsEvaluationSuite")]
public sealed class ObjectLockConfigurationRoundtrip
{
    /// <summary>
    /// PutObjectLockConfiguration on a bucket created with Object Lock enabled sets a default
    /// retention mode/period and GetObjectLockConfiguration returns it unchanged.
    /// </summary>
    [Test]
    [DisplayName("s3.object-lock-configuration-roundtrip")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var name = await S3Fidelity.CreateObjectLockBucket(ctx, "sim2real-behav-lockcfg-");

        await s3.PutObjectLockConfigurationAsync(new PutObjectLockConfigurationRequest
        {
            BucketName = name,
            ObjectLockConfiguration = new ObjectLockConfiguration
            {
                ObjectLockEnabled = ObjectLockEnabled.Enabled,
                Rule = new ObjectLockRule { DefaultRetention = new DefaultRetention { Mode = ObjectLockRetentionMode.Governance, Days = 1 } },
            },
        });

        var got = await s3.GetObjectLockConfigurationAsync(new GetObjectLockConfigurationRequest { BucketName = name });
        var cfg = got.ObjectLockConfiguration;
        Ensure(cfg.ObjectLockEnabled == "Enabled", $"expected ObjectLockEnabled='Enabled', got {Describe(cfg)}");
        var retention = cfg.Rule.DefaultRetention;
        Ensure(retention.Mode == "GOVERNANCE", $"expected DefaultRetention.Mode='GOVERNANCE', got {Describe(retention)}");
        Ensure(retention.Days == 1, $"expected DefaultRetention.Days=1, got {Describe(retention)}");
    }

    private static string Describe(ObjectLockConfiguration cfg) =>
        $"{{'ObjectLockEnabled': {Py.Repr(cfg.ObjectLockEnabled?.Value)}, 'Rule': {(cfg.Rule?.DefaultRetention is { } r ? Describe(r) : "None")}}}";

    private static string Describe(DefaultRetention retention) =>
        $"{{'Mode': {Py.Repr(retention.Mode?.Value)}, 'Days': {Py.Repr(retention.Days)}, 'Years': {Py.Repr(retention.Years)}}}";
}
