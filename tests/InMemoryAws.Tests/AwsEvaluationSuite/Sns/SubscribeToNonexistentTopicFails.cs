// Translated from behavioral/sns/check_subscribe_to_nonexistent_topic_fails.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.SimpleNotificationService.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.Sns;

/// <summary>sns.subscribe-to-nonexistent-topic-fails</summary>
[Category("AwsEvaluationSuite")]
public sealed class SubscribeToNonexistentTopicFails
{
    /// <summary>
    /// Subscribe against a nonexistent TopicArn must fail with NotFound, while the identical
    /// Subscribe call against the identical TopicArn succeeds once the topic is recreated under the
    /// same name.
    /// </summary>
    [Test]
    [DisplayName("sns.subscribe-to-nonexistent-topic-fails")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sns = ctx.Sns;
        var name = FidelityContext.UniqueName("sim2real-behav-sns-topic-");

        var arn = (await sns.CreateTopicAsync(new CreateTopicRequest { Name = name })).TopicArn;
        ctx.OnCleanup(() => sns.DeleteTopicAsync(new DeleteTopicRequest { TopicArn = arn }));

        // Positive control: the live topic accepts a subscription before anything is broken.
        var controlSub = (await sns.SubscribeAsync(new SubscribeRequest
        {
            TopicArn = arn, Protocol = "email", Endpoint = "probe-control@example.com"
        })).SubscriptionArn;
        Ensure(controlSub == "pending confirmation", $"expected literal 'pending confirmation' SubscriptionArn, got '{controlSub}'");

        // Break: delete the topic, then probe the now-nonexistent TopicArn.
        await sns.DeleteTopicAsync(new DeleteTopicRequest { TopicArn = arn });
        var (code, message) = await Error(() => sns.SubscribeAsync(new SubscribeRequest
        {
            TopicArn = arn, Protocol = "email", Endpoint = "probe-broken@example.com"
        }));
        Ensure(code == "NotFound", $"expected NotFound, got '{code}'");
        Ensure(message == "Topic does not exist", $"unexpected NotFound message for Subscribe: '{message}'");

        // Effect absent: an independent read (a different operation) sees no such topic either.
        var (listCode, listMessage) = await Error(() => sns.ListSubscriptionsByTopicAsync(new ListSubscriptionsByTopicRequest { TopicArn = arn }));
        Ensure(listCode == "NotFound", $"expected NotFound listing subscriptions on deleted topic, got '{listCode}'");
        Ensure(listMessage == "Topic does not exist", $"unexpected NotFound message listing subscriptions on deleted topic: '{listMessage}'");

        // Restore: recreating the topic under the identical name reuses the identical ARN...
        var restoredArn = (await sns.CreateTopicAsync(new CreateTopicRequest { Name = name })).TopicArn;
        ctx.OnCleanup(() => sns.DeleteTopicAsync(new DeleteTopicRequest { TopicArn = restoredArn }));
        Ensure(restoredArn == arn, $"expected the recreated topic to reuse '{arn}', got '{restoredArn}'");

        // ...and the identical Subscribe call now succeeds again.
        var restoredSub = (await sns.SubscribeAsync(new SubscribeRequest
        {
            TopicArn = restoredArn, Protocol = "email", Endpoint = "probe-restored@example.com"
        })).SubscriptionArn;
        Ensure(restoredSub == "pending confirmation", $"expected literal 'pending confirmation' SubscriptionArn on restore, got '{restoredSub}'");
    }

    /// <summary>Call fn(); return botocore (Error.Code, Error.Message) if a ClientError was raised, else raise.</summary>
    private static async Task<(string? Code, string? Message)> Error<T>(Func<Task<T>> fn)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        return error is null ? throw Fail($"expected a ClientError, call succeeded with {result}") : (error.Code, error.Message);
    }
}
