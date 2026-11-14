// Translated from behavioral/sns/check_list_subscriptions_pages_every_subscription_exactly_once.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.SimpleNotificationService.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sns;

/// <summary>sns.list-subscriptions-pages-every-subscription-exactly-once</summary>
[Category("AwsEvaluationSuite")]
public sealed class ListSubscriptionsPagesEverySubscriptionExactlyOnce
{
    private const string Scenario = "sns-list-subscriptions-paging";

    private const int Count = 101;

    /// <summary>
    /// Account-wide ListSubscriptions pages with NextToken, returns each of 101 subscriptions
    /// on one topic exactly once with its own Protocol, Endpoint, TopicArn and Owner, and drops
    /// one after it is unsubscribed.
    /// </summary>
    [Test]
    [DisplayName("sns.list-subscriptions-pages-every-subscription-exactly-once")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    [KnownFidelityGap(FidelityGap.SnsSubscriptions, "Subscribe to a well-formed SQS ARN of a queue that does not exist is refused with NotFound 'Queue not found.' instead of creating the subscription")]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sns = ctx.Sns;
        var topicArn = await CreateTopic(ctx);
        var prefix = FidelityContext.UniqueName("sim2real-behav-lsq-");
        var endpoints = new Dictionary<string, string>();
        for (var i = 0; i < Count; i++)
        {
            // SNS accepts a well-formed SQS ARN for a queue that does not exist.
            var endpoint = $"arn:aws:sqs:{ctx.Region}:{ctx.Account}:{prefix}-{i:000}";
            var sub = (await sns.SubscribeAsync(new SubscribeRequest
            {
                TopicArn = topicArn,
                Protocol = "sqs",
                Endpoint = endpoint,
                ReturnSubscriptionArn = true,
            })).SubscriptionArn;
            ctx.OnCleanup(() => ctx.Sns.UnsubscribeAsync(new UnsubscribeRequest { SubscriptionArn = sub }));
            endpoints[sub] = endpoint;
        }

        async Task<Walk> Ours()
        {
            var rows = new List<Subscription>();
            var sizes = new List<int>();
            string? token = null;
            while (true)
            {
                var request = new ListSubscriptionsRequest();
                if (!string.IsNullOrEmpty(token))
                {
                    request.NextToken = token;
                }

                var page = await sns.ListSubscriptionsAsync(request);
                sizes.Add((page.Subscriptions ?? []).Count);
                // The listing is account-wide; only this check's topic is asserted on.
                rows.AddRange((page.Subscriptions ?? []).Where(s => s.TopicArn == topicArn));
                token = page.NextToken;
                if (string.IsNullOrEmpty(token))
                {
                    return new Walk(rows, sizes, sizes.Count > 1);
                }
            }
        }

        static bool EachOnce(List<Subscription> rows, HashSet<string> wanted)
        {
            // A walk that overlaps another writer's Subscribe or Unsubscribe may shift page
            // boundaries; a walk is graded once it lists exactly the wanted rows, each once.
            var arns = rows.Select(s => s.SubscriptionArn).ToList();
            return arns.Count == arns.Distinct().Count() && arns.ToHashSet().SetEquals(wanted);
        }

        var (rows, sizes, paged) = await PollUntil(
            Ours, r => EachOnce(r.Rows, endpoints.Keys.ToHashSet()),
            timeoutS: 90.0, intervalS: 3.0,
            description: $"all {Count} subscriptions appear in ListSubscriptions");

        Ensure(paged, $"more than 100 subscriptions must span several pages, got page sizes {Ints(sizes)}");
        Ensure(sizes.Max() <= 100, $"a ListSubscriptions page held more than 100 entries: {Ints(sizes)}");
        var arns = rows.Select(s => s.SubscriptionArn).ToList();
        var distinct = arns.ToHashSet();
        Ensure(arns.Count == distinct.Count && distinct.Count == Count,
            $"each subscription must appear exactly once across pages; got {arns.Count} rows, " +
            $"{distinct.Count} distinct, expected {Count}");
        Ensure(distinct.SetEquals(endpoints.Keys),
            "the listed subscriptions must be exactly the ones created; " +
            $"{distinct.Except(endpoints.Keys).Count()} unknown, {endpoints.Keys.Except(distinct).Count()} missing");
        var wrong = rows
            .Where(s => s.Protocol != "sqs" || s.Endpoint != endpoints[s.SubscriptionArn] || s.Owner != ctx.Account)
            .Select(s => s.SubscriptionArn).ToList();
        Ensure(wrong.Count == 0,
            $"{wrong.Count} listed subscriptions do not carry their own Protocol 'sqs', Endpoint " +
            "and Owner (the account)");

        var gone = arns[0];
        await sns.UnsubscribeAsync(new UnsubscribeRequest { SubscriptionArn = gone });
        var remainingWanted = endpoints.Keys.Where(k => k != gone).ToHashSet();
        (rows, _, _) = await PollUntil(
            Ours, r => EachOnce(r.Rows, remainingWanted),
            timeoutS: 90.0, intervalS: 3.0,
            description: "the unsubscribed subscription leaves ListSubscriptions");
        var remaining = rows.Select(s => s.SubscriptionArn).ToHashSet();
        Ensure(remaining.SetEquals(remainingWanted),
            $"after one Unsubscribe the topic must list exactly the other {Count - 1}; got " +
            $"{remaining.Count}");
    }

    private sealed record Walk(List<Subscription> Rows, List<int> Sizes, bool Paged)
    {
        public override string ToString() => $"({Rows.Count} rows, page sizes {Ints(Sizes)}, paged={Paged})";
    }

    private static string Ints(IEnumerable<int> items) => "[" + string.Join(", ", items) + "]";

    private static List<Amazon.SimpleNotificationService.Model.Tag> TagsKv() =>
        [new() { Key = "sim2real", Value = Scenario }, new() { Key = "sim2real:managed", Value = "true" }];

    /// <summary>CreateTopic (tagged + prefix-named) and register DeleteTopic immediately.</summary>
    private static async Task<string> CreateTopic(FidelityContext ctx, bool fifo = false, Dictionary<string, string>? attributes = null)
    {
        var sns = ctx.Sns;
        var name = FidelityContext.UniqueName("sim2real-behav-sns-t-") + (fifo ? ".fifo" : "");
        var attrs = new Dictionary<string, string>(attributes ?? []);
        if (fifo)
        {
            attrs["FifoTopic"] = "true";
        }

        var arn = (await sns.CreateTopicAsync(new CreateTopicRequest { Name = name, Attributes = attrs, Tags = TagsKv() })).TopicArn;
        ctx.OnCleanup(() => ctx.Sns.DeleteTopicAsync(new DeleteTopicRequest { TopicArn = arn }));
        return arn;
    }

    /// <summary>Bounded retry; RAISES on the deadline so a timeout can never read as a pass.</summary>
    private static async Task<T> PollUntil<T>(Func<Task<T>> fn, Func<T, bool> pred, double timeoutS, double intervalS, string description)
    {
        var deadline = FidelityTime.StartNew();
        var value = await fn();
        while (!pred(value))
        {
            if (deadline.Elapsed >= TimeSpan.FromSeconds(timeoutS))
            {
                throw Fail($"{description}: not satisfied within {timeoutS}s, last value {value}");
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(intervalS));
            value = await fn();
        }

        return value;
    }
}
