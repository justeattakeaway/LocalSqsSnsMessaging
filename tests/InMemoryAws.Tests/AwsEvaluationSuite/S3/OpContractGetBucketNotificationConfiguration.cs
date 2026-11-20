// Translated from behavioral/s3/check_op_contract_get_bucket_notification_configuration.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.op-contract-get-bucket-notification-configuration</summary>
[Category("AwsEvaluationSuite")]
public sealed class OpContractGetBucketNotificationConfiguration
{
    /// <summary>S3's refusal of a bucket that does not exist, as real S3 returns it.</summary>
    private static readonly (string?, string?) NoSuchBucket = ("NoSuchBucket", "The specified bucket does not exist");

    /// <summary>GetBucketNotificationConfiguration reflects the configured EventBridge/queue/topic wiring.</summary>
    [Test]
    [DisplayName("s3.op-contract-get-bucket-notification-configuration")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var bucket = await S3Fidelity.CreateBucket(ctx, "sim2real-behav-", s3);

        // --- success path: an unconfigured bucket reports an empty configuration (no error) ---
        var gotEmpty = await s3.GetBucketNotificationAsync(bucket);
        Ensure(
            gotEmpty.EventBridgeConfiguration is null,
            $"expected no EventBridgeConfiguration on an unconfigured bucket, got {Describe(gotEmpty)}");
        Ensure(
            gotEmpty.QueueConfigurations is not { Count: > 0 },
            $"expected no QueueConfigurations on an unconfigured bucket, got {Describe(gotEmpty)}");
        Ensure(
            gotEmpty.TopicConfigurations is not { Count: > 0 },
            $"expected no TopicConfigurations on an unconfigured bucket, got {Describe(gotEmpty)}");
        Ensure(
            gotEmpty.LambdaFunctionConfigurations is not { Count: > 0 },
            $"expected no LambdaFunctionConfigurations on an unconfigured bucket, got {Describe(gotEmpty)}");

        // enabling EventBridge notifications requires no destination permissions, keeping this single-service
        await s3.PutBucketNotificationAsync(new PutBucketNotificationRequest
        {
            BucketName = bucket,
            EventBridgeConfiguration = new EventBridgeConfiguration(),
        });
        var got = await s3.GetBucketNotificationAsync(bucket);
        Ensure(got.EventBridgeConfiguration is not null, $"expected EventBridgeConfiguration present, got {Describe(got)}");

        // --- error boundary: operating against a bucket that does not exist ---
        var ghostBucket = FidelityContext.UniqueName("sim2real-behav-ghost-");
        var code = await Error(() => s3.GetBucketNotificationAsync(ghostBucket));
        Ensure(code == NoSuchBucket, $"expected NoSuchBucket, got {code}");
    }

    private static string Describe(GetBucketNotificationResponse response) =>
        $"{{EventBridgeConfiguration: {(response.EventBridgeConfiguration is null ? "absent" : "{}")}, " +
        $"QueueConfigurations: {response.QueueConfigurations?.Count ?? 0}, TopicConfigurations: {response.TopicConfigurations?.Count ?? 0}, " +
        $"LambdaFunctionConfigurations: {response.LambdaFunctionConfigurations?.Count ?? 0}}}";

    /// <summary>Return the ClientError (Code, Message) of fn(); raise AssertionError when fn() succeeds.</summary>
    private static async Task<(string?, string?)> Error(Func<Task> fn)
    {
        var err = await FidelityContext.CaptureError(fn);
        if (err is null)
        {
            throw Fail("expected a ClientError, but the call SUCCEEDED and returned {}");
        }

        return (err.Code, err.Message);
    }
}
