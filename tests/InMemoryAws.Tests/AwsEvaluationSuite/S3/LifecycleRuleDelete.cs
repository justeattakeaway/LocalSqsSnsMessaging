// Translated from behavioral/s3/check_lifecycle_rule_delete.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.lifecycle-rule-delete</summary>
[Category("AwsEvaluationSuite")]
public sealed class LifecycleRuleDelete
{
    /// <summary>DeleteBucketLifecycle removes lifecycle config.</summary>
    [Test]
    [DisplayName("s3.lifecycle-rule-delete")]
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
                        Id = "tmp-rule",
                        Status = LifecycleRuleStatus.Enabled,
                        Filter = new LifecycleFilter { Prefix = string.Empty },
                        Expiration = new LifecycleRuleExpiration { Days = 1 },
                    },
                ],
            },
        });
        var got = await s3.GetLifecycleConfigurationAsync(new GetLifecycleConfigurationRequest { BucketName = bucket });
        Ensure(got.Configuration.Rules[0].Id == "tmp-rule", $"expected rule 'tmp-rule' before deletion, got {Py.Repr(got.Configuration.Rules.Select(S3Fidelity.Boto).ToList())}");

        await s3.DeleteLifecycleConfigurationAsync(new DeleteLifecycleConfigurationRequest { BucketName = bucket });
        // The .NET SDK answers GetBucketLifecycleConfiguration's 404 as an empty success; the refusal is read off the wire.
        var err = await S3Fidelity.RawError(ctx, "GET", $"{bucket}?lifecycle", "expected a ClientError, but the call succeeded");
        Ensure(err.Code == "NoSuchLifecycleConfiguration", $"expected NoSuchLifecycleConfiguration after deletion, got {err}");
        Ensure(err.Message == "The lifecycle configuration does not exist", err.ToString());
    }
}
