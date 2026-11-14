// Translated from behavioral/sns/check_list_subscriptions_by_topic_reflects_state.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.SimpleNotificationService;
using Amazon.SimpleNotificationService.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sns;

/// <summary>sns.list-subscriptions-by-topic-reflects-state</summary>
[Category("AwsEvaluationSuite")]
public sealed class ListSubscriptionsByTopicReflectsState
{
    /// <summary>ListSubscriptionsByTopic reflects current subscription set after subscribe/unsubscribe.</summary>
    [Test]
    [DisplayName("sns.list-subscriptions-by-topic-reflects-state")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    [KnownFidelityGap(FidelityGap.SnsSubscriptions, "Subscribe to a well-formed SQS ARN of a queue that does not exist is refused with NotFound 'Queue not found.' instead of creating the subscription")]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ev = ctx.Sns;
        var topicArn = await CreateTopic(ev, ctx, FidelityContext.UniqueName("sim2real-behav-sns-listsub-"));
        var sub1 = await SubscribeFakeQueue(ev, ctx, topicArn, FakeQueueArn(topicArn, FidelityContext.UniqueName("sim2real-behav-sns-listsub-q1-")));
        var sub2 = await SubscribeFakeQueue(ev, ctx, topicArn, FakeQueueArn(topicArn, FidelityContext.UniqueName("sim2real-behav-sns-listsub-q2-")));
        Ensure(sub1 != sub2, "expected distinct endpoints to produce distinct subscriptions");
        var arns = ((await ev.ListSubscriptionsByTopicAsync(new ListSubscriptionsByTopicRequest { TopicArn = topicArn })).Subscriptions ?? [])
            .Select(s => s.SubscriptionArn).ToHashSet();
        Ensure(new[] { sub1, sub2 }.All(arns.Contains), $"expected both subscriptions listed, got {{{string.Join(", ", arns.Select(a => $"'{a}'"))}}}");
        await ev.UnsubscribeAsync(new UnsubscribeRequest { SubscriptionArn = sub1 });
        var arnsAfter = ((await ev.ListSubscriptionsByTopicAsync(new ListSubscriptionsByTopicRequest { TopicArn = topicArn })).Subscriptions ?? [])
            .Select(s => s.SubscriptionArn).ToHashSet();
        Ensure(!arnsAfter.Contains(sub1), $"expected {sub1} removed from listing after unsubscribe");
        Ensure(arnsAfter.Contains(sub2), $"expected {sub2} to remain listed");
    }

    /// <summary>CreateTopic + register best-effort DeleteTopic cleanup; returns the TopicArn.</summary>
    private static async Task<string> CreateTopic(AmazonSimpleNotificationServiceClient client, FidelityContext ctx, string name)
    {
        var arn = (await client.CreateTopicAsync(new CreateTopicRequest { Name = name })).TopicArn;
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

    /// <summary>Subscribe an sqs-protocol endpoint; register unsubscribe cleanup; return the SubscriptionArn.</summary>
    private static async Task<string> SubscribeFakeQueue(AmazonSimpleNotificationServiceClient client, FidelityContext ctx, string topicArn,
        string? queueArn = null)
    {
        queueArn ??= FakeQueueArn(topicArn);
        var subArn = (await client.SubscribeAsync(new SubscribeRequest { TopicArn = topicArn, Protocol = "sqs", Endpoint = queueArn })).SubscriptionArn;
        if (!string.IsNullOrEmpty(subArn) && subArn != "pending confirmation")
        {
            ctx.OnCleanup(() => client.UnsubscribeAsync(new UnsubscribeRequest { SubscriptionArn = subArn }));
        }

        return subArn;
    }
}
