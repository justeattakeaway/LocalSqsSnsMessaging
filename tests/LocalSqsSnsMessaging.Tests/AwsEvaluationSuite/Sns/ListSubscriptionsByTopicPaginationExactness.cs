// Translated from behavioral/sns/check_list_subscriptions_by_topic_pagination_exactness.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.SimpleNotificationService;
using Amazon.SimpleNotificationService.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sns;

/// <summary>sns.list-subscriptions-by-topic-pagination-exactness</summary>
[Category("AwsEvaluationSuite")]
public sealed class ListSubscriptionsByTopicPaginationExactness
{
    private const string Check = "list-subscriptions-by-topic-pagination-exactness";

    private const int SubscriptionCount = 105; // > the 100-item SNS list page size, so >= 2 pages are required

    /// <summary>
    /// NextToken paging over 105 subscriptions is exact (>= 2 pages, no duplicates, omissions or
    /// overlap, no trailing token) and a garbage NextToken is refused with InvalidParameter.
    /// Page order and page size are not asserted: SNS documents neither.
    /// </summary>
    [Test]
    [DisplayName("sns.list-subscriptions-by-topic-pagination-exactness")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    [KnownFidelityGap(FidelityGap.SnsSubscriptions, "Subscribe to a well-formed SQS ARN of a queue that does not exist is refused with NotFound 'Queue not found.' instead of creating the subscription")]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sns = ctx.Sns;
        var topicArn = await CreateTopic(sns, ctx, FidelityContext.UniqueName("sim2real-behav-sns-paging-"));

        var created = new HashSet<string>();
        for (var i = 0; i < SubscriptionCount; i++)
        {
            var endpoint = FakeQueueArn(topicArn, $"sim2real-behav-sns-paging-q-{i:000}-{Guid.NewGuid().ToString("N")[..8]}");
            var arn = (await sns.SubscribeAsync(new SubscribeRequest { TopicArn = topicArn, Protocol = "sqs", Endpoint = endpoint })).SubscriptionArn;
            Ensure(!string.IsNullOrEmpty(arn) && arn != "pending confirmation", $"expected subscription {i} to auto-confirm, got '{arn}'");
            created.Add(arn);
        }

        Ensure(created.Count == SubscriptionCount, $"expected {SubscriptionCount} distinct SubscriptionArns, got {created.Count}");

        var pages = await Poll(
            () => Pages(sns, topicArn),
            p => p.Sum(x => x.Count) >= SubscriptionCount,
            timeoutS: 60.0,
            intervalS: 3.0,
            what: $"all {SubscriptionCount} subscriptions become visible across pages");

        Ensure(pages.Count >= 2,
            $"{SubscriptionCount} subscriptions must not fit in one page; got {pages.Count} page(s) " +
            $"of sizes {Sizes(pages)}");

        var flat = pages.SelectMany(page => page).ToList();
        Ensure(flat.Count == SubscriptionCount,
            "paging must return each subscription exactly once; expected " +
            $"{SubscriptionCount} rows across {pages.Count} pages, got {flat.Count} " +
            $"(page sizes {Sizes(pages)})");
        Ensure(flat.Distinct().Count() == flat.Count,
            "paging returned a duplicated SubscriptionArn: " +
            $"{Repr(flat.Distinct().Where(a => flat.Count(f => f == a) > 1).Order(StringComparer.Ordinal).Take(5))}");
        Ensure(flat.ToHashSet().SetEquals(created),
            "the union of all pages must be exactly the created set; " +
            $"missing {Repr(created.Except(flat).Order(StringComparer.Ordinal).Take(5))}, " +
            $"unexpected {Repr(flat.ToHashSet().Except(created).Order(StringComparer.Ordinal).Take(5))}");
        for (var i = 0; i < pages.Count - 1; i++)
        {
            var overlap = pages[i].Intersect(pages[i + 1]).ToList();
            Ensure(overlap.Count == 0, $"page {i} and page {i + 1} must be disjoint; overlapping {Repr(overlap.Order(StringComparer.Ordinal).Take(5))}");
        }

        // The final page must not carry a NextToken -- _pages() terminated only because it did
        // not, so re-assert the boundary explicitly against the last request.
        string? lastToken = null;
        string? token = null;
        for (var i = 0; i < pages.Count; i++)
        {
            var request = new ListSubscriptionsByTopicRequest { TopicArn = topicArn };
            if (!string.IsNullOrEmpty(token))
            {
                request.NextToken = token;
            }

            var resp = await sns.ListSubscriptionsByTopicAsync(request);
            lastToken = resp.NextToken;
            token = lastToken;
        }

        Ensure(lastToken is null, $"the final page must omit NextToken, got '{lastToken}'");

        // Break exactly one thing: a NextToken that was never issued.
        var (code, message) = await Err(() => sns.ListSubscriptionsByTopicAsync(new ListSubscriptionsByTopicRequest
        {
            TopicArn = topicArn,
            NextToken = "sim2real-behav-not-a-real-token",
        }));
        Ensure(code == "InvalidParameter", $"expected InvalidParameter for a NextToken that was never issued, got {code}");
        Ensure(message == "Invalid parameter: NextToken", $"expected the exact InvalidParameter message AWS returns, got '{message}'");

        // The un-tokened first page holds the same ARNs as before, so the
        // rejected call did not consume, invalidate, or advance any server-side cursor.
        var firstAgain = ((await sns.ListSubscriptionsByTopicAsync(new ListSubscriptionsByTopicRequest { TopicArn = topicArn })).Subscriptions ?? [])
            .Select(s => s.SubscriptionArn).ToList();
        Ensure(firstAgain.ToHashSet().SetEquals(pages[0]),
            "a rejected NextToken must not disturb the unpaginated listing; expected the same " +
            $"{pages[0].Count} ARNs, got {firstAgain.Count}");
    }

    private static string Repr(IEnumerable<string> items) => "[" + string.Join(", ", items.Select(s => $"'{s}'")) + "]";

    private static string Sizes(List<List<string>> pages) => "[" + string.Join(", ", pages.Select(p => p.Count)) + "]";

    /// <summary>
    /// CreateTopic (tagged) and register DeleteTopic cleanup; returns the TopicArn. DeleteTopic
    /// removes every subscription on the topic, so it is the only teardown the subscriptions need.
    /// </summary>
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

    /// <summary>
    /// A well-formed SQS queue ARN in the topic's account/region (SNS confirms sqs
    /// subscriptions without verifying the queue exists, keeping this single-service and
    /// making 105 subscribers free to create).
    /// </summary>
    private static string FakeQueueArn(string topicArn, string? name = null)
    {
        var parts = topicArn.Split(':');
        name ??= $"sim2real-behav-sns-q-{Guid.NewGuid().ToString("N")[..8]}";
        return $"arn:aws:sqs:{parts[3]}:{parts[4]}:{name}";
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
                throw Fail($"{what}: condition not met within {timeoutS}s, last value {val}");
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(intervalS));
            val = await fn();
        }

        return val;
    }

    /// <summary>Walk ListSubscriptionsByTopic with NextToken; return the list of per-page ARN lists.</summary>
    private static async Task<List<List<string>>> Pages(AmazonSimpleNotificationServiceClient client, string topicArn, int maxPages = 20)
    {
        var pages = new List<List<string>>();
        string? token = null;
        for (var i = 0; i < maxPages; i++)
        {
            var request = new ListSubscriptionsByTopicRequest { TopicArn = topicArn };
            if (!string.IsNullOrEmpty(token))
            {
                request.NextToken = token;
            }

            var resp = await client.ListSubscriptionsByTopicAsync(request);
            pages.Add((resp.Subscriptions ?? []).Select(s => s.SubscriptionArn).ToList());
            token = resp.NextToken;
            if (string.IsNullOrEmpty(token))
            {
                return pages;
            }
        }

        throw Fail($"ListSubscriptionsByTopic still returned a NextToken after {maxPages} pages -- " +
            "the token is not advancing");
    }

    /// <summary>Call fn(); return the (Code, Message) of its ClientError. An unexpected success raises.</summary>
    private static async Task<(string? Code, string? Message)> Err<T>(Func<Task<T>> fn)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is null)
        {
            throw Fail($"expected a rejection, but the call SUCCEEDED and returned {result}");
        }

        return (error.Code, error.Message);
    }
}
