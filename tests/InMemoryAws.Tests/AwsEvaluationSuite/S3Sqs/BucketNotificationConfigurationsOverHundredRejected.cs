// Translated from behavioral/s3__sqs/check_bucket_notification_configurations_over_hundred_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit abd0811).
using Amazon.S3;
using Amazon.S3.Model;
using Amazon.SQS.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3Sqs;

/// <summary>s3__sqs.bucket-notification-configurations-over-hundred-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class BucketNotificationConfigurationsOverHundredRejected
{
    /// <summary>Event notification configurations are capped at a fixed, non-adjustable 100 per bucket.</summary>
    private const int MaxConfigs = 100;

    /// <summary>S3's refusal of a notification configuration set past that cap.</summary>
    private static readonly (string? Code, string? Message) TooManyConfigs =
        ("InvalidRequest", $"Too many configurations submitted. The maximum number of configurations is {MaxConfigs}");

    /// <summary>
    /// PutBucketNotificationConfiguration accepts exactly 100 configurations, the fixed
    /// non-adjustable per-bucket cap, and rejects a 101st without replacing the stored set.
    /// </summary>
    [Test]
    [DisplayName("s3__sqs.bucket-notification-configurations-over-hundred-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var sqs = ctx.Sqs;
        var bucket = await S3Fidelity.CreateBucket(ctx, "sim2real-behav-");

        var queueUrl = (await sqs.CreateQueueAsync(FidelityContext.UniqueName("sim2real-behav-q-"))).QueueUrl;
        ctx.OnCleanup(() => S3Fidelity.NotificationDeleteQueue(sqs, queueUrl));
        var queueArn = await S3Fidelity.NotificationQueueArn(sqs, queueUrl);
        await sqs.SetQueueAttributesAsync(new SetQueueAttributesRequest
        {
            QueueUrl = queueUrl,
            Attributes = new Dictionary<string, string> { ["Policy"] = S3Fidelity.NotificationAllowS3Policy(queueArn, bucket) },
        });

        await s3.PutBucketNotificationAsync(new PutBucketNotificationRequest { BucketName = bucket, QueueConfigurations = QueueConfigs(queueArn, MaxConfigs) });
        ctx.OnCleanup(() => s3.PutBucketNotificationAsync(new PutBucketNotificationRequest { BucketName = bucket }));
        var stored = (await s3.GetBucketNotificationAsync(new GetBucketNotificationRequest { BucketName = bucket })).QueueConfigurations ?? [];
        Ensure(stored.Count == MaxConfigs, $"{MaxConfigs} configurations, the documented cap, did not round-trip: got {stored.Count}");

        var got = await S3Fidelity.ErrorPair(() => s3.PutBucketNotificationAsync(new PutBucketNotificationRequest
        {
            BucketName = bucket,
            QueueConfigurations = QueueConfigs(queueArn, MaxConfigs + 1),
        }));
        Ensure(got == TooManyConfigs, $"expected {S3Fidelity.Repr(TooManyConfigs)} for 101 configurations, got {S3Fidelity.Repr(got)}");

        var survived = (await s3.GetBucketNotificationAsync(new GetBucketNotificationRequest { BucketName = bucket })).QueueConfigurations ?? [];
        Ensure(survived.Count == MaxConfigs, $"the rejected 101-configuration write must not have mutated the stored set, got {survived.Count}");
    }

    /// <summary><c>_queue_configs(queue_arn, count)</c>: <paramref name="count"/> distinct configurations on one queue, each its own key prefix.</summary>
    private static List<QueueConfiguration> QueueConfigs(string queueArn, int count) =>
        [.. Enumerable.Range(0, count).Select(i => new QueueConfiguration
        {
            Id = $"cfg{i}",
            Queue = queueArn,
            Events = ["s3:ObjectCreated:*"],
            Filter = new Filter { S3KeyFilter = new S3KeyFilter { FilterRules = [new FilterRule { Name = "prefix", Value = $"p{i}/" }] } },
        })];
}
