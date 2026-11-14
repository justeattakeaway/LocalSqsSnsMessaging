// Translated from behavioral/sns/check_set_subscription_attributes_nonexistent_fails.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.SimpleNotificationService.Model;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sns;

/// <summary>sns.set-subscription-attributes-nonexistent-fails</summary>
[Category("AwsEvaluationSuite")]
public sealed class SetSubscriptionAttributesNonexistentFails
{
    /// <summary>
    /// [overly-permissive] SetSubscriptionAttributes on a nonexistent ARN must fail with NotFound, while
    /// the identical call against a real subscription succeeds and is independently readable back.
    /// </summary>
    [Test]
    [DisplayName("sns.set-subscription-attributes-nonexistent-fails")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sns = ctx.Sns;
        var topicArn = await CreateTopic(ctx);

        // Positive control: the real, existing subscription accepts the identical mutation, and an
        // independent read (GetSubscriptionAttributes, not the mutating SetSubscriptionAttributes
        // response) confirms it took effect.
        var realSub = await CreateSqsSubscription(ctx, topicArn);
        await sns.SetSubscriptionAttributesAsync(new SetSubscriptionAttributesRequest
        {
            SubscriptionArn = realSub, AttributeName = "RawMessageDelivery", AttributeValue = "true",
        });
        var controlAttrs = (await sns.GetSubscriptionAttributesAsync(new GetSubscriptionAttributesRequest { SubscriptionArn = realSub })).Attributes ?? [];
        Ensure(controlAttrs.GetValueOrDefault("RawMessageDelivery") == "true",
            $"positive control: expected RawMessageDelivery='true' via independent read, got {Repr(controlAttrs)}");

        // Break: a well-formed SubscriptionArn that was never created.
        var fakeSub = FakeSubscriptionArn(topicArn);
        var (code, message) = await Error(() => sns.SetSubscriptionAttributesAsync(new SetSubscriptionAttributesRequest
        {
            SubscriptionArn = fakeSub, AttributeName = "RawMessageDelivery", AttributeValue = "true",
        }));
        Ensure(code == "NotFound", $"expected NotFound, got '{code}'");
        Ensure(message == "Subscription does not exist", $"expected the exact message 'Subscription does not exist', got '{message}'");

        // Effect absent, via an independent read at the destination (GetSubscriptionAttributes, a
        // different operation than the SetSubscriptionAttributes call under test).
        var (readCode, readMessage) = await Error(() => sns.GetSubscriptionAttributesAsync(new GetSubscriptionAttributesRequest { SubscriptionArn = fakeSub }));
        Ensure(readCode == "NotFound", $"expected NotFound reading attributes of a nonexistent subscription, got '{readCode}'");
        Ensure(readMessage == "Subscription does not exist", $"expected the exact message 'Subscription does not exist', got '{readMessage}'");
    }

    private static string Repr(Dictionary<string, string> d) =>
        "{" + string.Join(", ", d.Select(kv => $"'{kv.Key}': '{kv.Value}'")) + "}";

    /// <summary>Call fn(); return the botocore error (Code, Message) if a ClientError was raised, else raise.</summary>
    private static async Task<(string? Code, string? Message)> Error<T>(Func<Task<T>> fn)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is null)
        {
            throw Fail($"expected a ClientError, call succeeded with {result}");
        }

        return (error.Code, error.Message);
    }

    /// <summary>Create a topic, register its teardown, and return its ARN.</summary>
    private static async Task<string> CreateTopic(FidelityContext ctx, string? name = null)
    {
        var sns = ctx.Sns;
        name ??= FidelityContext.UniqueName("sim2real-behav-sns-topic-");
        var arn = (await sns.CreateTopicAsync(new CreateTopicRequest { Name = name })).TopicArn;
        ctx.OnCleanup(() => sns.DeleteTopicAsync(new DeleteTopicRequest { TopicArn = arn }));
        return arn;
    }

    /// <summary>Create an SQS queue and subscribe it to the topic; return the real, confirmed SubscriptionArn.</summary>
    private static async Task<string> CreateSqsSubscription(FidelityContext ctx, string topicArn)
    {
        var sqs = ctx.Sqs;
        var sns = ctx.Sns;
        var queueUrl = (await sqs.CreateQueueAsync(new CreateQueueRequest { QueueName = FidelityContext.UniqueName("sim2real-behav-sns-subattr-q-") })).QueueUrl;
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(queueUrl));
        var queueArn = (await sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest { QueueUrl = queueUrl, AttributeNames = ["QueueArn"] })).Attributes!["QueueArn"];
        var subArn = (await sns.SubscribeAsync(new SubscribeRequest { TopicArn = topicArn, Protocol = "sqs", Endpoint = queueArn })).SubscriptionArn;
        ctx.OnCleanup(() => sns.UnsubscribeAsync(new UnsubscribeRequest { SubscriptionArn = subArn }));
        return subArn;
    }

    /// <summary>Return a well-formed but nonexistent SubscriptionArn for a topic.</summary>
    private static string FakeSubscriptionArn(string topicArn) => $"{topicArn}:{Guid.NewGuid()}";
}
