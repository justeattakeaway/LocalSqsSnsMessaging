// Translated from behavioral/sns/check_list_topics_paginates_every_created_topic_exactly_once.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.SimpleNotificationService;
using Amazon.SimpleNotificationService.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sns;

/// <summary>sns.list-topics-paginates-every-created-topic-exactly-once</summary>
[Category("AwsEvaluationSuite")]
public sealed class ListTopicsPaginatesEveryCreatedTopicExactlyOnce
{
    private const string Check = "list-topics-paginates-every-created-topic-exactly-once";
    private const int NTopics = 105; // more than one ListTopics page (SNS pages at 100 topics)
    private const int PageMax = 100;

    /// <summary>
    /// ListTopics pages at most 100 topics per response and a NextToken walk returns every
    /// one of 105 freshly created topics exactly once.
    /// </summary>
    [Test]
    [DisplayName("sns.list-topics-paginates-every-created-topic-exactly-once")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sns = ctx.Sns;
        var prefix = FidelityContext.UniqueName("sim2real-behav-sns-ltpage-");
        List<Tag> tagsKv = [new() { Key = "sim2real", Value = Check }];
        var created = new List<string>();
        for (var i = 0; i < NTopics; i++)
        {
            var arn = (await sns.CreateTopicAsync(new CreateTopicRequest { Name = $"{prefix}-{i:000}", Tags = tagsKv })).TopicArn;
            ctx.OnCleanup(() => sns.DeleteTopicAsync(new DeleteTopicRequest { TopicArn = arn }));
            created.Add(arn);
        }

        Ensure(created.ToHashSet().Count == NTopics,
            $"{NTopics} distinct names must yield {NTopics} distinct TopicArns, got {created.ToHashSet().Count}");

        var mine = created.ToHashSet(StringComparer.Ordinal);

        bool Consistent(List<List<string>> ps)
        {
            // A walk that overlaps another writer's CreateTopic or DeleteTopic may shift page
            // boundaries; a walk is graded once it lists each created topic exactly once and
            // page 2 does not repeat page 1's last topic.
            var seen = ps.SelectMany(p => p).Where(mine.Contains).ToList();
            var resumes = ps.Count < 2 || !(ps[0].Count > 0 && ps[1].Count > 0) || ps[0][^1] != ps[1][0];
            return seen.Count == seen.ToHashSet().Count && seen.ToHashSet().SetEquals(mine) && resumes;
        }

        var pages = await Poll(
            () => Walk(sns),
            Consistent,
            timeoutS: 60.0, intervalS: 3.0,
            what: "every created topic appears in a full ListTopics walk");

        var oversized = pages.Where(p => p.Count > PageMax).Select(p => p.Count).ToList();
        Ensure(oversized.Count == 0, $"ListTopics returned pages larger than {PageMax} topics: sizes [{string.Join(", ", oversized)}]");
        Ensure(pages.Count >= 2,
            $"the account holds at least {NTopics} topics, so ListTopics must paginate; " +
            $"got {pages.Count} page(s)");
        var flat = pages.SelectMany(p => p).ToList();
        var seenMine = flat.Where(mine.Contains).ToList();
        var dupes = seenMine.GroupBy(a => a).Where(g => g.Count() > 1).Select(g => g.Key).Order(StringComparer.Ordinal).ToList();
        Ensure(dupes.Count == 0, $"{dupes.Count} created topic(s) appeared on more than one page of one walk");
        Ensure(seenMine.Count == NTopics,
            $"a full walk must return each of the {NTopics} created topics exactly once, " +
            $"saw {seenMine.Count} occurrences");
        // A page's NextToken resumes after that page: the first page's last topic is never the
        // second page's first.
        Ensure(pages[0].Count > 0 && pages[1].Count > 0 && pages[0][^1] != pages[1][0],
            "page 2 must resume after page 1, not repeat its last topic");
    }

    /// <summary>Walk ListTopics by hand with NextToken; return the list of per-page TopicArn lists.</summary>
    private static async Task<List<List<string>>> Walk(AmazonSimpleNotificationServiceClient sns, int maxPages = 500)
    {
        var pages = new List<List<string>>();
        string? token = null;
        for (var i = 0; i < maxPages; i++)
        {
            var request = new ListTopicsRequest();
            if (!string.IsNullOrEmpty(token))
            {
                request.NextToken = token;
            }

            var resp = await sns.ListTopicsAsync(request);
            pages.Add((resp.Topics ?? []).Select(t => t.TopicArn).ToList());
            token = resp.NextToken;
            if (string.IsNullOrEmpty(token))
            {
                return pages;
            }
        }

        throw Fail($"ListTopics still returned a NextToken after {maxPages} pages");
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
}
