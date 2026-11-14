// Translated from behavioral/sns/check_op_set_subscription_attributes_invalid_name_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.SimpleNotificationService;
using Amazon.SimpleNotificationService.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sns;

/// <summary>sns.op-set-subscription-attributes-invalid-name-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class OpSetSubscriptionAttributesInvalidNameRejected
{
    private const string Check = "op-set-subscription-attributes-invalid-name-rejected";

    /// <summary>SetSubscriptionAttributes must reject attribute names that are not user-settable (e.g. TopicArn).</summary>
    [Test]
    [DisplayName("sns.op-set-subscription-attributes-invalid-name-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sns = ctx.Sns;
        var topicArn = await CreateTopic(sns, ctx, FidelityContext.UniqueName("sim2real-behav-sns-subattr-bad-"));
        var queueArn = FakeQueueArn(topicArn);
        var subArn = (await sns.SubscribeAsync(new SubscribeRequest { TopicArn = topicArn, Protocol = "sqs", Endpoint = queueArn })).SubscriptionArn;
        if (!string.IsNullOrEmpty(subArn) && subArn != "pending confirmation")
        {
            ctx.OnCleanup(() => sns.UnsubscribeAsync(new UnsubscribeRequest { SubscriptionArn = subArn }));
        }

        // Positive control: a genuinely user-settable attribute must succeed on this same
        // subscription, so the rejection below can be attributed to TopicArn specifically
        // rather than to a broken subscription or a SetSubscriptionAttributes call that fails
        // unconditionally.
        await sns.SetSubscriptionAttributesAsync(new SetSubscriptionAttributesRequest
        {
            SubscriptionArn = subArn, AttributeName = "RawMessageDelivery", AttributeValue = "true",
        });
        var control = (await sns.GetSubscriptionAttributesAsync(new GetSubscriptionAttributesRequest { SubscriptionArn = subArn })).Attributes ?? [];
        Ensure(control.GetValueOrDefault("RawMessageDelivery") == "true",
            $"positive control failed: RawMessageDelivery not applied, got '{control.GetValueOrDefault("RawMessageDelivery")}'");

        var (code, message) = await Error(() => sns.SetSubscriptionAttributesAsync(new SetSubscriptionAttributesRequest
        {
            SubscriptionArn = subArn, AttributeName = "TopicArn", AttributeValue = "hijacked",
        }));
        Ensure(code == "InvalidParameter",
            $"expected InvalidParameter when setting a non-settable subscription attribute, got {code}");
        const string expected = "Invalid parameter: AttributeName";
        Ensure(message == expected, $"expected message '{expected}', got '{message}'");

        var attrs = (await sns.GetSubscriptionAttributesAsync(new GetSubscriptionAttributesRequest { SubscriptionArn = subArn })).Attributes!;
        Ensure(attrs["TopicArn"] == topicArn, "rejected SetSubscriptionAttributes must not mutate state");
        Ensure(attrs["RawMessageDelivery"] == "true",
            "rejected SetSubscriptionAttributes must not disturb the prior, successfully-set state");
    }

    /// <summary>
    /// Call fn(); return (code, message) of the ClientError it raises. An unexpected success
    /// raises, so a target that accepts what AWS refuses never reads as a code mismatch.
    /// </summary>
    private static async Task<(string? Code, string? Message)> Error<T>(Func<Task<T>> fn)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is null)
        {
            throw Fail($"expected the call to be refused, but it succeeded and returned {result}");
        }

        return (error.Code, error.Message);
    }

    /// <summary>CreateTopic (tagged) + register best-effort DeleteTopic cleanup; returns the TopicArn.</summary>
    private static async Task<string> CreateTopic(AmazonSimpleNotificationServiceClient client, FidelityContext ctx, string name,
        Dictionary<string, string>? attributes = null)
    {
        var request = new CreateTopicRequest
        {
            Name = name,
            Tags = [new() { Key = "sim2real", Value = Check }, new() { Key = "sim2real:managed", Value = "true" }],
        };
        if (attributes is { Count: > 0 })
        {
            request.Attributes = attributes;
        }

        var arn = (await client.CreateTopicAsync(request)).TopicArn;
        ctx.OnCleanup(() => client.DeleteTopicAsync(new DeleteTopicRequest { TopicArn = arn }));
        return arn;
    }

    /// <summary>A well-formed SQS queue ARN in the topic's account/region (SNS confirms sqs subs without verifying the queue exists).</summary>
    private static string FakeQueueArn(string topicArn, string? name = null)
    {
        var parts = topicArn.Split(':');
        name ??= $"sim2real-behav-sns-q-{Guid.NewGuid().ToString("N")[..8]}";
        return $"arn:aws:sqs:{parts[3]}:{parts[4]}:{name}";
    }
}
