// Translated from behavioral/sns/check_list_subscriptions_paginates_and_drops_unsubscribed.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.SimpleNotificationService;
using Amazon.SimpleNotificationService.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sns;

/// <summary>sns.list-subscriptions-paginates-and-drops-unsubscribed</summary>
[Category("AwsEvaluationSuite")]
public sealed class ListSubscriptionsPaginatesAndDropsUnsubscribed
{
    private const string Check = "list-subscriptions-paginates-and-drops-unsubscribed";
    private const int NSubs = 105; // more than one ListSubscriptions page (SNS pages at 100 subscriptions)
    private const int PageMax = 100;

    /// <summary>Walk account-wide ListSubscriptions by hand; return the list of per-page entry lists.</summary>
    private static async Task<List<List<Subscription>>> Walk(AmazonSimpleNotificationServiceClient sns, int maxPages = 2000)
    {
        var pages = new List<List<Subscription>>();
        string? token = null;
        for (var i = 0; i < maxPages; i++)
        {
            var request = new ListSubscriptionsRequest();
            if (!string.IsNullOrEmpty(token))
            {
                request.NextToken = token;
            }

            var resp = await sns.ListSubscriptionsAsync(request);
            pages.Add(resp.Subscriptions ?? []);
            token = resp.NextToken;
            if (string.IsNullOrEmpty(token))
            {
                return pages;
            }
        }

        throw Fail($"ListSubscriptions still returned a NextToken after {maxPages} pages");
    }

    /// <summary>Poll fn() until pred(val); raise AssertionError on deadline miss (never a bare sleep).</summary>
    private static async Task<T> Poll<T>(Func<Task<T>> fn, Func<T, bool> pred, double timeoutS, double intervalS, string what)
    {
        var deadline = FidelityTime.StartNew();
        var val = await fn();
        while (!pred(val))
        {
            if (deadline.Elapsed >= TimeSpan.FromSeconds(timeoutS))
            {
                throw Fail($"{what}: condition not met within {timeoutS}s");
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(intervalS));
            val = await fn();
        }

        return val;
    }

    /// <summary>
    /// Account-wide ListSubscriptions pages at most 100 entries, a NextToken walk returns each
    /// of 105 new subscriptions exactly once with its Owner, Protocol, Endpoint and TopicArn,
    /// and an unsubscribed one leaves the listing while its siblings stay.
    /// </summary>
    [Test]
    [DisplayName("sns.list-subscriptions-paginates-and-drops-unsubscribed")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    [KnownFidelityGap(FidelityGap.SnsSubscriptions, "Subscribe to the ARN of a queue that does not exist is refused with NotFound 'Queue not found.' instead of creating the subscription")]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sns = ctx.Sns;
        var topicArn = (await sns.CreateTopicAsync(new CreateTopicRequest
        {
            Name = FidelityContext.UniqueName("sim2real-behav-sns-lspage-"),
            Tags = [new() { Key = "sim2real", Value = Check }],
        })).TopicArn;
        ctx.OnCleanup(() => sns.DeleteTopicAsync(new DeleteTopicRequest { TopicArn = topicArn }));
        var (region, account) = (topicArn.Split(':')[3], topicArn.Split(':')[4]);
        var qprefix = FidelityContext.UniqueName("sim2real-behav-sns-lspage-q-");

        var endpoints = new Dictionary<string, string>();
        for (var i = 0; i < NSubs; i++)
        {
            var endpoint = $"arn:aws:sqs:{region}:{account}:{qprefix}-{i:000}";
            var subArn = (await sns.SubscribeAsync(new SubscribeRequest
            {
                TopicArn = topicArn,
                Protocol = "sqs",
                Endpoint = endpoint,
                ReturnSubscriptionArn = true,
            })).SubscriptionArn;
            ctx.OnCleanup(() => sns.UnsubscribeAsync(new UnsubscribeRequest { SubscriptionArn = subArn }));
            endpoints[subArn] = endpoint;
        }

        Ensure(endpoints.Count == NSubs, $"expected {NSubs} distinct SubscriptionArns, got {endpoints.Count}");

        var mine = endpoints.Keys.ToHashSet();

        bool EachOnce(List<List<Subscription>> ps, HashSet<string> wanted)
        {
            // A walk that overlaps another writer's Subscribe or Unsubscribe may shift page
            // boundaries; a walk is graded once it lists exactly the wanted members of `mine`, each once.
            var arns = ps.SelectMany(p => p).Select(e => e.SubscriptionArn).Where(mine.Contains).ToList();
            return arns.Count == arns.Distinct().Count() && arns.ToHashSet().SetEquals(wanted);
        }

        var pages = await Poll(
            () => Walk(sns),
            ps => EachOnce(ps, mine),
            timeoutS: 90.0, intervalS: 3.0,
            what: "every created subscription appears in a full ListSubscriptions walk");
        var oversized = pages.Where(p => p.Count > PageMax).Select(p => p.Count).ToList();
        Ensure(oversized.Count == 0, $"ListSubscriptions returned pages larger than {PageMax}: sizes [{string.Join(", ", oversized)}]");
        Ensure(pages.Count >= 2,
            $"the account holds at least {NSubs} subscriptions, so ListSubscriptions must " +
            $"paginate; got {pages.Count} page(s)");
        var entries = pages.SelectMany(p => p).Where(e => mine.Contains(e.SubscriptionArn)).ToList();
        Ensure(entries.Count == NSubs,
            $"a full walk must return each of the {NSubs} subscriptions exactly once, " +
            $"saw {entries.Count} occurrences");
        var wrong = entries.Where(e =>
            e.TopicArn != topicArn || e.Protocol != "sqs"
            || e.Owner != ctx.Account || e.Endpoint != endpoints[e.SubscriptionArn]).ToList();
        Ensure(wrong.Count == 0,
            $"{wrong.Count} listed subscription(s) carry the wrong TopicArn/Protocol/Owner/Endpoint; " +
            $"first fields: {(wrong.Count > 0 ? Fields(wrong[0]) : "None")}");

        var gone = mine.Order(StringComparer.Ordinal).First();
        await sns.UnsubscribeAsync(new UnsubscribeRequest { SubscriptionArn = gone });
        var wantedAfter = mine.Where(s => s != gone).ToHashSet();
        var after = await Poll(
            () => Walk(sns),
            ps => EachOnce(ps, wantedAfter),
            timeoutS: 90.0, intervalS: 3.0,
            what: "the unsubscribed subscription leaves account-wide ListSubscriptions");
        var remaining = after.SelectMany(p => p).Select(e => e.SubscriptionArn).Where(mine.Contains).ToHashSet();
        Ensure(remaining.SetEquals(wantedAfter),
            $"unsubscribing one subscription must leave its {NSubs - 1} siblings listed, " +
            $"{remaining.Count} remain");
    }

    /// <summary>sorted(entry): the names of the fields the listing entry carries.</summary>
    private static string Fields(Subscription e)
    {
        var names = new List<string>();
        if (e.Endpoint is not null)
        {
            names.Add("Endpoint");
        }

        if (e.Owner is not null)
        {
            names.Add("Owner");
        }

        if (e.Protocol is not null)
        {
            names.Add("Protocol");
        }

        if (e.SubscriptionArn is not null)
        {
            names.Add("SubscriptionArn");
        }

        if (e.TopicArn is not null)
        {
            names.Add("TopicArn");
        }

        return "[" + string.Join(", ", names.Select(n => $"'{n}'")) + "]";
    }
}
