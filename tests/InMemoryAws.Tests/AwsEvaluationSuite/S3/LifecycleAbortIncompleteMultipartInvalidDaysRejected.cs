// Translated from behavioral/s3/check_lifecycle_abort_incomplete_multipart_invalid_days_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.lifecycle-abort-incomplete-multipart-invalid-days-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class LifecycleAbortIncompleteMultipartInvalidDaysRejected
{
    /// <summary>
    /// A lifecycle rule with AbortIncompleteMultipartUpload DaysAfterInitiation=0 is rejected with
    /// InvalidArgument and the existing valid rule (DaysAfterInitiation=1) stays unchanged.
    /// </summary>
    [Test]
    [DisplayName("s3.lifecycle-abort-incomplete-multipart-invalid-days-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var bucket = await S3Fidelity.CreateBucket(ctx, "sim2real-behav-bkt-");

        const string validRuleId = "sim2real-valid-abort-mpu-rule";
        await s3.PutLifecycleConfigurationAsync(new PutLifecycleConfigurationRequest
        {
            BucketName = bucket,
            Configuration = new LifecycleConfiguration { Rules = [Rule(validRuleId, "mpu-valid-", 1)] },
        });
        var positiveCfg = await s3.GetLifecycleConfigurationAsync(new GetLifecycleConfigurationRequest { BucketName = bucket });
        var positiveRule = positiveCfg.Configuration.Rules.Where(r => r.Id == validRuleId).Single();
        Ensure(
            positiveRule.AbortIncompleteMultipartUpload?.DaysAfterInitiation == 1,
            $"positive control: expected the valid DaysAfterInitiation=1 rule reflected back, got {Py.Repr(S3Fidelity.Boto(positiveRule))}");

        var err = await FidelityContext.CaptureError(() => s3.PutLifecycleConfigurationAsync(new PutLifecycleConfigurationRequest
        {
            BucketName = bucket,
            Configuration = new LifecycleConfiguration { Rules = [Rule("sim2real-invalid-abort-mpu-rule", "mpu-invalid-", 0)] },
        })) ?? throw Fail("expected a ClientError, but the call succeeded");
        Ensure(err.Code == "InvalidArgument", $"expected InvalidArgument for DaysAfterInitiation=0, got {err}");
        Ensure(err.Message == "'DaysAfterInitiation' for AbortIncompleteMultipartUpload action must be a positive integer", err.ToString());

        var cfg = await s3.GetLifecycleConfigurationAsync(new GetLifecycleConfigurationRequest { BucketName = bucket });
        var ruleIds = cfg.Configuration.Rules.Select(r => r.Id).ToHashSet(StringComparer.Ordinal);
        Ensure(
            ruleIds.SetEquals([validRuleId]),
            $"expected the rejected rule to never have been committed and the prior valid rule untouched, got rule ids {Py.Repr(ruleIds.ToList())}");
        var survivingRule = cfg.Configuration.Rules.Where(r => r.Id == validRuleId).Single();
        Ensure(
            survivingRule.AbortIncompleteMultipartUpload?.DaysAfterInitiation == 1,
            $"expected the pre-existing valid rule left unmodified by the rejected put, got {Py.Repr(S3Fidelity.Boto(survivingRule))}");
    }

    private static LifecycleRule Rule(string id, string prefix, int days) => new()
    {
        Id = id,
        Filter = new LifecycleFilter { Prefix = prefix },
        Status = LifecycleRuleStatus.Enabled,
        AbortIncompleteMultipartUpload = new LifecycleRuleAbortIncompleteMultipartUpload { DaysAfterInitiation = days },
    };
}
