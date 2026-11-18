// Translated from behavioral/sns/check_topic_attributes_subscription_counters_track_state.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Globalization;
using Amazon.SimpleNotificationService;
using Amazon.SimpleNotificationService.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.Sns;

/// <summary>sns.topic-attributes-subscription-counters-track-state</summary>
[Category("AwsEvaluationSuite")]
public sealed class TopicAttributesSubscriptionCountersTrackState
{
    private const string Check = "topic-attributes-subscription-counters-track-state";

    private sealed record Counters(int Confirmed, int Pending, int Deleted)
    {
        public override string ToString() => $"{{'confirmed': {Confirmed}, 'pending': {Pending}, 'deleted': {Deleted}}}";
    }

    /// <summary>
    /// GetTopicAttributes' SubscriptionsConfirmed, SubscriptionsPending and SubscriptionsDeleted
    /// start at 0 and follow an auto-confirmed sqs Subscribe, a pending email Subscribe and an
    /// Unsubscribe, whose ARN is then rejected with NotFound.
    /// </summary>
    [Test]
    [DisplayName("sns.topic-attributes-subscription-counters-track-state")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sns = ctx.Sns;
        // The counters are computed at read time and may lag a write, so each transition is polled.
        var topicArn = await CreateTopic(sns, ctx, FidelityContext.UniqueName("sim2real-behav-sns-counters-"));

        var start = await GetCounters(sns, topicArn);
        Ensure(start == new Counters(0, 0, 0), $"a freshly created topic must report all three counters at 0, got {start}");

        // An sqs subscription auto-confirms, so Confirmed goes to 1 and Pending stays 0.
        var sqsEndpoint = FakeQueueArn(topicArn, FidelityContext.UniqueName("sim2real-behav-sns-counters-q-"));
        var subArn = (await sns.SubscribeAsync(new SubscribeRequest { TopicArn = topicArn, Protocol = "sqs", Endpoint = sqsEndpoint })).SubscriptionArn;
        ctx.OnCleanup(CleanupUnsubscribe(sns, subArn));

        var afterConfirm = await Poll(() => GetCounters(sns, topicArn), c => c.Confirmed == 1, 60.0, 2.0,
            "SubscriptionsConfirmed reaches 1 after an auto-confirming sqs Subscribe");
        Ensure(afterConfirm.Pending == 0, $"an auto-confirmed sqs subscription must not be counted as pending, got {afterConfirm}");
        Ensure(afterConfirm.Deleted == 0, $"nothing has been unsubscribed yet, so SubscriptionsDeleted must still be 0, got {afterConfirm}");

        // An email subscription requires out-of-band confirmation, so Pending goes to 1 while
        // Confirmed holds at 1. `@example.com` is IANA-reserved; the confirmation email itself
        // is never read and nothing about it is asserted.
        await sns.SubscribeAsync(new SubscribeRequest { TopicArn = topicArn, Protocol = "email", Endpoint = "sim2real-behav-sns@example.com" });
        var afterPending = await Poll(() => GetCounters(sns, topicArn), c => c.Pending == 1, 60.0, 2.0,
            "SubscriptionsPending reaches 1 after a confirmation-requiring Subscribe");
        Ensure(afterPending.Confirmed == 1,
            "a new PENDING subscription must not be counted as confirmed; expected confirmed=1, " +
            $"got {afterPending}");

        // Unsubscribing the confirmed one moves it out of Confirmed and into Deleted.
        await sns.UnsubscribeAsync(new UnsubscribeRequest { SubscriptionArn = subArn });
        var afterDelete = await Poll(() => GetCounters(sns, topicArn), c => c.Confirmed == 0, 60.0, 2.0,
            "SubscriptionsConfirmed returns to 0 after Unsubscribe");
        Ensure(afterDelete.Pending == 1, $"unsubscribing the confirmed subscription must not disturb the pending one, got {afterDelete}");

        var (code, message) = await Error(() => sns.GetSubscriptionAttributesAsync(new GetSubscriptionAttributesRequest { SubscriptionArn = subArn }));
        Ensure(code == "NotFound", $"GetSubscriptionAttributes on the unsubscribed ARN must be rejected with NotFound, got '{code}'");
        Ensure(message == "Subscription does not exist", $"unexpected NotFound message for GetSubscriptionAttributes: '{message}'");

        var deletedSeen = await Poll(() => GetCounters(sns, topicArn), c => c.Deleted >= 1, 60.0, 2.0,
            "SubscriptionsDeleted increments after Unsubscribe");
        Ensure(deletedSeen.Deleted >= 1, $"SubscriptionsDeleted must account for the unsubscribed subscription, got {deletedSeen}");
    }

    private static List<Tag> TagsKv() =>
        [new Tag { Key = "sim2real", Value = Check }, new Tag { Key = "sim2real:managed", Value = "true" }];

    /// <summary>CreateTopic (tagged, so a tag search finds it) + register best-effort DeleteTopic cleanup; returns the TopicArn.</summary>
    private static async Task<string> CreateTopic(AmazonSimpleNotificationServiceClient client, FidelityContext ctx, string name)
    {
        var arn = (await client.CreateTopicAsync(new CreateTopicRequest { Name = name, Tags = TagsKv() })).TopicArn;
        ctx.OnCleanup(() => client.DeleteTopicAsync(new DeleteTopicRequest { TopicArn = arn }));
        return arn;
    }

    /// <summary>The AWS error (Code, Message) raised by fn(). Raises if fn SUCCEEDS.</summary>
    private static async Task<(string? Code, string? Message)> Error<T>(Func<Task<T>> fn)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        return error is null
            ? throw Fail($"the call was expected to be rejected but SUCCEEDED, returning {result}")
            : (error.Code, error.Message);
    }

    /// <summary>A well-formed SQS queue ARN in the topic's account/region (SNS confirms sqs subscriptions without verifying the queue exists).</summary>
    private static string FakeQueueArn(string topicArn, string name)
    {
        var parts = topicArn.Split(':');
        return $"arn:aws:sqs:{parts[3]}:{parts[4]}:{name}";
    }

    /// <summary>Poll fn() until pred(val); raise on deadline miss (never a bare sleep).</summary>
    private static async Task<T> Poll<T>(Func<Task<T>> fn, Func<T, bool> pred, double timeoutS, double intervalS, string what)
    {
        var deadline = FidelityTime.StartNew();
        var val = await fn();
        while (!pred(val))
        {
            if (deadline.Elapsed >= TimeSpan.FromSeconds(timeoutS))
            {
                throw Fail($"{what}: condition not met within {timeoutS}s, last value {val}");
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(intervalS));
            val = await fn();
        }

        return val;
    }

    /// <summary>A subscription-first teardown that tolerates ONLY 'already gone'.</summary>
    private static Func<Task> CleanupUnsubscribe(AmazonSimpleNotificationServiceClient client, string subArn) => async () =>
    {
        try
        {
            await client.UnsubscribeAsync(new UnsubscribeRequest { SubscriptionArn = subArn });
        }
        catch (AmazonSimpleNotificationServiceException ex) when (ex.ErrorCode is "NotFound" or "InvalidParameter")
        {
        }
    };

    /// <summary>The three computed subscription counters, as ints. A missing counter is a defect.</summary>
    private static async Task<Counters> GetCounters(AmazonSimpleNotificationServiceClient client, string topicArn)
    {
        var attrs = (await client.GetTopicAttributesAsync(new GetTopicAttributesRequest { TopicArn = topicArn })).Attributes ?? [];
        var missing = new[] { "SubscriptionsConfirmed", "SubscriptionsPending", "SubscriptionsDeleted" }.Where(k => !attrs.ContainsKey(k)).ToList();
        Ensure(missing.Count == 0,
            $"GetTopicAttributes must report the computed counters [{string.Join(", ", missing)}] on every topic; got [{string.Join(", ", attrs.Keys.Order(StringComparer.Ordinal))}]");
        return new Counters(
            int.Parse(attrs["SubscriptionsConfirmed"], CultureInfo.InvariantCulture),
            int.Parse(attrs["SubscriptionsPending"], CultureInfo.InvariantCulture),
            int.Parse(attrs["SubscriptionsDeleted"], CultureInfo.InvariantCulture));
    }
}
