// Translated from behavioral/s3/check_op_contract_get_bucket_lifecycle_configuration.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.op-contract-get-bucket-lifecycle-configuration</summary>
[Category("AwsEvaluationSuite")]
public sealed class OpContractGetBucketLifecycleConfiguration
{
    /// <summary>
    /// GetBucketLifecycleConfiguration returns configured rules; an unconfigured bucket raises NoSuchLifecycleConfiguration.
    /// </summary>
    [Test]
    [DisplayName("s3.op-contract-get-bucket-lifecycle-configuration")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var bucket = await S3Fidelity.CreateBucket(ctx, "sim2real-behav-");

        // --- error boundary first: fresh bucket has no lifecycle configuration ---
        // The .NET SDK answers GetBucketLifecycleConfiguration's 404 as an empty success; the refusal is read off the wire.
        var (code, message) = S3Fidelity.Botocore(await S3Fidelity.RawError(ctx, "GET", $"{bucket}?lifecycle", "expected a ClientError, but the call SUCCEEDED"));
        Ensure(code == "NoSuchLifecycleConfiguration", $"expected NoSuchLifecycleConfiguration, got {Py.Repr(code)}");
        Ensure(message == "The lifecycle configuration does not exist", $"unexpected message {Py.Repr(message)}");

        // --- success path: put a rule, read it back verbatim ---
        await s3.PutLifecycleConfigurationAsync(new PutLifecycleConfigurationRequest
        {
            BucketName = bucket,
            Configuration = new LifecycleConfiguration
            {
                Rules =
                [
                    new LifecycleRule
                    {
                        Id = "expire-tmp",
                        Filter = new LifecycleFilter { LifecycleFilterPredicate = new LifecyclePrefixPredicate { Prefix = "tmp/" } },
                        Status = LifecycleRuleStatus.Enabled,
                        Expiration = new LifecycleRuleExpiration { Days = 7 },
                    },
                ],
            },
        });
        var got = await s3.GetLifecycleConfigurationAsync(new GetLifecycleConfigurationRequest { BucketName = bucket });
        var rule = got.Configuration.Rules[0];
        Ensure(rule.Id == "expire-tmp", $"expected rule id 'expire-tmp', got {Py.Repr(rule.Id)}");
        Ensure(rule.Status == "Enabled", $"expected Status 'Enabled', got {Py.Repr(rule.Status?.Value)}");
        var prefix = (rule.Filter?.LifecycleFilterPredicate as LifecyclePrefixPredicate)?.Prefix;
        Ensure(prefix == "tmp/", $"expected Filter.Prefix 'tmp/', got {Py.Repr(prefix)}");
        Ensure(rule.Expiration?.Days == 7, $"expected Expiration.Days 7, got {Py.Repr(rule.Expiration?.Days)}");
    }
}
