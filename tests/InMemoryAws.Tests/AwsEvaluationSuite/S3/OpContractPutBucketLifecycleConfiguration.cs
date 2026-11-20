// Translated from behavioral/s3/check_op_contract_put_bucket_lifecycle_configuration.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.op-contract-putBucketLifecycleConfiguration</summary>
[Category("AwsEvaluationSuite")]
public sealed class OpContractPutBucketLifecycleConfiguration
{
    /// <summary>S3's refusal of a bucket that does not exist, as real S3 returns it.</summary>
    private static readonly (string, string) NoSuchBucket = ("NoSuchBucket", "The specified bucket does not exist");

    private const string BucketPrefix = "sim2real-behav-";

    /// <summary>
    /// PutBucketLifecycleConfiguration persists rules (filter/transition/expiration); errors NoSuchBucket when missing.
    /// </summary>
    [Test]
    [DisplayName("s3.op-contract-putBucketLifecycleConfiguration")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var bucket = await S3Fidelity.CreateBucket(ctx, BucketPrefix);
        var s3 = ctx.S3;

        List<LifecycleRule> rules =
        [
            new LifecycleRule
            {
                Id = "expire-logs",
                Status = LifecycleRuleStatus.Enabled,
                Filter = new LifecycleFilter { Prefix = "logs/" },
                Expiration = new LifecycleRuleExpiration { Days = 30 },
            },
            new LifecycleRule
            {
                Id = "abort-incomplete-uploads",
                Status = LifecycleRuleStatus.Enabled,
                Filter = new LifecycleFilter { Prefix = string.Empty },
                AbortIncompleteMultipartUpload = new LifecycleRuleAbortIncompleteMultipartUpload { DaysAfterInitiation = 7 },
            },
        ];
        await s3.PutLifecycleConfigurationAsync(new PutLifecycleConfigurationRequest { BucketName = bucket, Configuration = new LifecycleConfiguration { Rules = rules } });
        ctx.OnCleanup(() => s3.DeleteLifecycleConfigurationAsync(new DeleteLifecycleConfigurationRequest { BucketName = bucket }));

        var resp = await s3.GetLifecycleConfigurationAsync(new GetLifecycleConfigurationRequest { BucketName = bucket });
        var gotById = resp.Configuration.Rules.Select(S3Fidelity.Boto).ToDictionary(r => (string)r["ID"]!, StringComparer.Ordinal);
        Ensure(
            gotById.Keys.ToHashSet().SetEquals(["expire-logs", "abort-incomplete-uploads"]),
            $"unexpected rule ids: dict_keys({Py.Repr(gotById.Keys.ToList())})");
        var expireDays = ((Dictionary<string, object?>)gotById["expire-logs"]["Expiration"]!)["Days"];
        Ensure(Equals(expireDays, 30), $"expected expire-logs Expiration.Days == 30, got {expireDays}");
        var expirePrefix = ((Dictionary<string, object?>)gotById["expire-logs"]["Filter"]!)["Prefix"];
        Ensure(Equals(expirePrefix, "logs/"), $"expected expire-logs Filter.Prefix == 'logs/', got {Py.Repr(expirePrefix)}");
        var abortDays = ((Dictionary<string, object?>)gotById["abort-incomplete-uploads"]["AbortIncompleteMultipartUpload"]!)["DaysAfterInitiation"];
        Ensure(Equals(abortDays, 7), $"expected abort-incomplete-uploads DaysAfterInitiation == 7, got {abortDays}");

        // distinct error boundary: nonexistent bucket
        var missing = FidelityContext.UniqueName(BucketPrefix + "missing-");
        var code = await S3Fidelity.ClientError(
            () => s3.PutLifecycleConfigurationAsync(new PutLifecycleConfigurationRequest { BucketName = missing, Configuration = new LifecycleConfiguration { Rules = rules } }));
        Ensure(code == NoSuchBucket, $"expected NoSuchBucket, got {Py.Repr(code)}");
    }
}
