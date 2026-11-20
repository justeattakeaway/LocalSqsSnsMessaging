// Translated from behavioral/s3/check_lifecycle_transition_server_side.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.lifecycle-transition-server-side</summary>
[Category("AwsEvaluationSuite")]
public sealed class LifecycleTransitionServerSide
{
    /// <summary>
    /// A Transition lifecycle rule (prefix transition-, STANDARD_IA after 30 days) is accepted and read
    /// back exactly by GetBucketLifecycleConfiguration, and an object written under it starts in
    /// STANDARD.
    /// </summary>
    [Test]
    [DisplayName("s3.lifecycle-transition-server-side")]
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
                        Id = "sim2real-transition-rule",
                        Filter = new LifecycleFilter { Prefix = "transition-" },
                        Status = LifecycleRuleStatus.Enabled,
                        Transitions = [new LifecycleTransition { Days = 30, StorageClass = S3StorageClass.StandardInfrequentAccess }],
                    },
                ],
            },
        });

        var cfg = await s3.GetLifecycleConfigurationAsync(new GetLifecycleConfigurationRequest { BucketName = bucket });
        var rule = S3Fidelity.Boto(cfg.Configuration.Rules.Where(r => r.Id == "sim2real-transition-rule").Single());
        Ensure(Equals(rule["Status"], "Enabled"), $"expected Status Enabled, got {Py.Repr(rule["Status"])}");
        var filter = (Dictionary<string, object?>)rule["Filter"]!;
        Ensure(Equals(filter.GetValueOrDefault("Prefix"), "transition-"), $"expected Filter Prefix 'transition-', got {Py.Repr(filter.GetValueOrDefault("Prefix"))}");
        var transition = ((List<Dictionary<string, object?>>)rule["Transitions"]!)[0];
        Ensure(Equals(transition["StorageClass"], "STANDARD_IA"), $"expected transition StorageClass STANDARD_IA, got {Py.Repr(transition["StorageClass"])}");
        Ensure(Equals(transition["Days"], 30), $"expected transition Days 30, got {Py.Repr(transition["Days"])}");

        const string key = "transition-object.txt";
        await s3.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = key, ContentBody = "pre-transition-payload" });

        var head = await s3.GetObjectMetadataAsync(bucket, key);
        // S3 omits StorageClass entirely for the STANDARD default.
        var storageClass = head.StorageClass?.Value;
        Ensure(
            storageClass is null or "STANDARD",
            "a freshly PUT object matched by a (not-yet-elapsed) transition rule must still "
            + $"report STANDARD, got {Py.Repr(storageClass)}");
    }
}
