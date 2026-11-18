// Translated from behavioral/sns/check_topic_subscription_counters_converge_after_concurrent_subscribes_and_unsubscribes.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Globalization;
using Amazon.SimpleNotificationService;
using Amazon.SimpleNotificationService.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.Sns;

/// <summary>sns.topic-subscription-counters-converge-after-concurrent-subscribes-and-unsubscribes</summary>
[Category("AwsEvaluationSuite")]
public sealed class TopicSubscriptionCountersConvergeAfterConcurrentSubscribesAndUnsubscribes
{
    private const string Check = "topic-subscription-counters-converge-after-concurrent-subscribes-and-unsubscribes";
    private const int Confirmed = 16;
    private const int Pending = 4;
    private const int Removed = 10;

    /// <summary>
    /// Twenty concurrent subscribes (16 auto-confirming sqs, 4 pending email) converge the topic's
    /// SubscriptionsConfirmed/SubscriptionsPending counters to 16/4, matching the listing; ten
    /// concurrent unsubscribes of confirmed ones bring them to 6/4 and the listing to the survivors.
    /// </summary>
    [Test]
    [DisplayName("sns.topic-subscription-counters-converge-after-concurrent-subscribes-and-unsubscribes")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sns = ctx.Sns;
        var topic = (await sns.CreateTopicAsync(new CreateTopicRequest
        {
            Name = FidelityContext.UniqueName("sim2real-behav-sns-ctrs-"),
            Tags = [new Tag { Key = "sim2real", Value = Check }],
        })).TopicArn;
        ctx.OnCleanup(() => sns.DeleteTopicAsync(new DeleteTopicRequest { TopicArn = topic }));
        var parts = topic.Split(':');
        var (region, account) = (parts[3], parts[4]);
        var tag = Guid.NewGuid().ToString("N")[..8];
        var queues = Enumerable.Range(0, Confirmed).Select(i => $"arn:aws:sqs:{region}:{account}:sim2real-behav-sns-ctrs-{tag}-{i}").ToList();
        var mails = Enumerable.Range(0, Pending).Select(i => $"sim2real-behav-sns-{tag}-{i}@example.com").ToList();

        async Task<string> Subscribe((string Protocol, string Endpoint) target) =>
            (await sns.SubscribeAsync(new SubscribeRequest
            {
                TopicArn = topic, Protocol = target.Protocol, Endpoint = target.Endpoint, ReturnSubscriptionArn = true
            })).SubscriptionArn;

        var targets = queues.Select(q => ("sqs", q)).Concat(mails.Select(m => ("email", m))).ToList();
        string[] arns;
        using (ctx.Concurrently())
        {
            arns = await Task.WhenAll(targets.Select(t => Task.Run(() => Subscribe(t))));
        }

        var confirmed = arns.Take(Confirmed).ToList();
        foreach (var arn in confirmed)
        {
            ctx.OnCleanup(() => UnsubscribeIfPresent(sns, arn));
        }

        Ensure(confirmed.Distinct().Count() == Confirmed, $"{Confirmed} sqs subscribes returned {confirmed.Distinct().Count()} distinct ARNs");

        await Converge(sns, topic, (Confirmed, Pending), "after the concurrent subscribes");
        var listed = await Listed(sns, topic);
        Ensure(listed.Select(s => s.Endpoint).Order(StringComparer.Ordinal).SequenceEqual(queues.Concat(mails).Order(StringComparer.Ordinal)),
            $"the topic must list every endpoint once; it lists {listed.Count} subscription(s)");
        Ensure(listed.Where(s => s.Protocol == "sqs").Select(s => s.SubscriptionArn).Order(StringComparer.Ordinal).SequenceEqual(confirmed.Order(StringComparer.Ordinal)),
            "the listed sqs subscriptions are not the ARNs Subscribe returned");

        using (ctx.Concurrently())
        {
            await Task.WhenAll(confirmed.Take(Removed).Select(arn => Task.Run(() => sns.UnsubscribeAsync(new UnsubscribeRequest { SubscriptionArn = arn }))));
        }

        await Converge(sns, topic, (Confirmed - Removed, Pending), "after the concurrent unsubscribes");
        var remaining = await Listed(sns, topic);
        Ensure(remaining.Where(s => s.Protocol == "sqs").Select(s => s.SubscriptionArn).Order(StringComparer.Ordinal)
                .SequenceEqual(confirmed.Skip(Removed).Order(StringComparer.Ordinal)),
            $"after removing {Removed}, the topic must list exactly the survivors; it lists " +
            $"{remaining.Count(s => s.Protocol == "sqs")} sqs subscription(s)");
        Ensure(remaining.Where(s => s.Protocol == "email").Select(s => s.Endpoint).Order(StringComparer.Ordinal).SequenceEqual(mails.Order(StringComparer.Ordinal)),
            "the concurrent unsubscribes disturbed the pending email subscriptions");
    }

    private static async Task<(int, int)> GetCounters(AmazonSimpleNotificationServiceClient sns, string topic)
    {
        var attrs = (await sns.GetTopicAttributesAsync(new GetTopicAttributesRequest { TopicArn = topic })).Attributes;
        return (int.Parse(attrs["SubscriptionsConfirmed"], CultureInfo.InvariantCulture),
            int.Parse(attrs["SubscriptionsPending"], CultureInfo.InvariantCulture));
    }

    private static async Task Converge(AmazonSimpleNotificationServiceClient sns, string topic, (int, int) want, string what)
    {
        var deadline = FidelityTime.StartNew();
        var seen = await GetCounters(sns, topic);
        while (seen != want)
        {
            Ensure(deadline.Elapsed < TimeSpan.FromSeconds(120),
                $"{what}: (SubscriptionsConfirmed, SubscriptionsPending) never reached {want}; last {seen}");
            await FidelityTime.Delay(TimeSpan.FromSeconds(3));
            seen = await GetCounters(sns, topic);
        }
    }

    private static async Task<List<Subscription>> Listed(AmazonSimpleNotificationServiceClient sns, string topic)
    {
        var all = new List<Subscription>();
        string? token = null;
        do
        {
            var page = await sns.ListSubscriptionsByTopicAsync(new ListSubscriptionsByTopicRequest { TopicArn = topic, NextToken = token });
            all.AddRange(page.Subscriptions ?? []);
            token = page.NextToken;
        }
        while (!string.IsNullOrEmpty(token));

        return all;
    }

    /// <summary>Teardown for a subscription the check may already have removed: tolerates only that.</summary>
    private static async Task UnsubscribeIfPresent(AmazonSimpleNotificationServiceClient sns, string arn)
    {
        try
        {
            await sns.UnsubscribeAsync(new UnsubscribeRequest { SubscriptionArn = arn });
        }
        catch (AmazonSimpleNotificationServiceException ex) when (ex.ErrorCode == "NotFound")
        {
        }
    }
}
