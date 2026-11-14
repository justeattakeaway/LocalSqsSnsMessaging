// Translated from behavioral/sqs/check_list_queues_max_results_paginates_with_next_token.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Diagnostics;
using Amazon.SQS;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sqs;

/// <summary>sqs.list-queues-max-results-paginates-with-next-token</summary>
[Category("AwsEvaluationSuite")]
public sealed class ListQueuesMaxResultsPaginatesWithNextToken
{
    private const int N = 5;
    private const int Page = 2;

    private static readonly Dictionary<string, string> Tags = new()
    {
        ["sim2real"] = "list-queues-max-results-paginates-with-next-token",
    };

    /// <summary>
    /// ListQueues with MaxResults pages a prefix's queues through NextToken.
    ///
    /// Pages hold at most MaxResults, are disjoint, union to the created queues, and end tokenless.
    /// </summary>
    [Test]
    [DisplayName("sqs.list-queues-max-results-paginates-with-next-token")]
    [Timeout(FidelityContext.BudgetMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sqs = ctx.Sqs;
        var prefix = FidelityContext.UniqueName("sim2real-behav-lqp-");
        HashSet<string> expected = [];
        for (var i = 0; i < N; i++)
        {
            var url = (await sqs.CreateQueueAsync(new CreateQueueRequest { QueueName = $"{prefix}-{i}", Tags = new(Tags) })).QueueUrl;
            ctx.OnCleanup(() => sqs.DeleteQueueAsync(url));
            expected.Add(url);
        }

        // ListQueues is eventually consistent with CreateQueue: wait until an unpaged listing of the
        // prefix shows every queue before grading the paged walk.
        var deadline = Stopwatch.StartNew();
        HashSet<string> seen;
        while (true)
        {
            seen = [.. (await sqs.ListQueuesAsync(new ListQueuesRequest { QueueNamePrefix = prefix, MaxResults = 1000 })).QueueUrls ?? []];
            if (seen.SetEquals(expected) || deadline.Elapsed > TimeSpan.FromSeconds(90))
            {
                break;
            }

            await Task.Delay(TimeSpan.FromSeconds(2));
        }

        Ensure(seen.SetEquals(expected),
            $"an unpaged ListQueues of the prefix never converged on the {N} created queues: " +
            $"{seen.Count} listed, {seen.Intersect(expected).Count()} of them ours");

        var pages = await AllPages(sqs, prefix, Page);
        var sizes = pages.Select(p => p.Urls.Count).ToList();
        Ensure(sizes.All(s => s <= Page),
            $"a ListQueues page exceeded MaxResults={Page}: page sizes [{string.Join(", ", sizes)}]");
        Ensure(pages.Count >= (N + Page - 1) / Page,
            $"{N} queues at MaxResults={Page} need at least {(N + Page - 1) / Page} pages, " +
            $"got {pages.Count} with sizes [{string.Join(", ", sizes)}]");
        Ensure(pages[..^1].All(p => !string.IsNullOrEmpty(p.NextToken)),
            "every page but the last must carry a NextToken; tokens present per page: " +
            $"[{string.Join(", ", pages.Select(p => !string.IsNullOrEmpty(p.NextToken)))}]");
        Ensure(pages[^1].NextToken is null, "the last ListQueues page still carried a NextToken");
        var flat = pages.SelectMany(p => p.Urls).ToList();
        var flatSet = flat.ToHashSet();
        Ensure(flat.Count == flatSet.Count,
            $"pagination repeated a queue: {flat.Count} URLs over the pages, {flatSet.Count} distinct");
        Ensure(flatSet.SetEquals(expected),
            $"the paged walk returned {flatSet.Count} URLs, {flatSet.Intersect(expected).Count()} of the " +
            $"{N} created; missing {expected.Except(flatSet).Count()}, foreign {flatSet.Except(expected).Count()}");
    }

    /// <summary>Every page ListQueues returns for ``prefix`` at ``page_size``, following NextToken.</summary>
    private static async Task<List<(List<string> Urls, string? NextToken)>> AllPages(AmazonSQSClient sqs, string prefix, int pageSize)
    {
        List<(List<string> Urls, string? NextToken)> pages = [];
        var request = new ListQueuesRequest { QueueNamePrefix = prefix, MaxResults = pageSize };
        for (var i = 0; i < 50; i++)
        {
            var resp = await sqs.ListQueuesAsync(request);
            pages.Add((resp.QueueUrls ?? [], resp.NextToken));
            var token = resp.NextToken;
            if (string.IsNullOrEmpty(token))
            {
                return pages;
            }

            request.NextToken = token;
        }

        throw Fail($"ListQueues pagination did not terminate after 50 pages: {pages.Count} pages");
    }
}
