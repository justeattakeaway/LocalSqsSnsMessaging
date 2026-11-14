// Translated from behavioral/sqs/check_list_dead_letter_source_queues_paginates.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Diagnostics;
using Amazon.SQS;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sqs;

/// <summary>sqs.list-dead-letter-source-queues-paginates</summary>
[Category("AwsEvaluationSuite")]
public sealed class ListDeadLetterSourceQueuesPaginates
{
    private static readonly Dictionary<string, string> Tags = new()
    {
        ["sim2real"] = "list-dead-letter-source-queues-paginates",
    };

    private const int N = 3;

    /// <summary>
    /// ListDeadLetterSourceQueues with MaxResults=1 pages a DLQ's sources one URL per page.
    ///
    /// Every page but the last has a NextToken; the union is exactly the queues redriving to that DLQ.
    /// </summary>
    [Test]
    [DisplayName("sqs.list-dead-letter-source-queues-paginates")]
    [Timeout(FidelityContext.BudgetMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sqs = ctx.Sqs;
        var baseName = FidelityContext.UniqueName("sim2real-behav-ldp-");

        async Task<(string Url, string Arn)> Queue(string suffix)
        {
            var url = (await sqs.CreateQueueAsync(new CreateQueueRequest { QueueName = $"{baseName}-{suffix}", Tags = new(Tags) })).QueueUrl;
            ctx.OnCleanup(() => sqs.DeleteQueueAsync(url));
            var arn = (await sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest
            {
                QueueUrl = url, AttributeNames = ["QueueArn"],
            })).Attributes!["QueueArn"];
            return (url, arn);
        }

        var (dlqUrl, dlqArn) = await Queue("dlq");
        var (otherDlqUrl, otherDlqArn) = await Queue("dlq2");
        HashSet<string> expected = [];
        for (var i = 0; i < N; i++)
        {
            var (url, _) = await Queue($"s{i}");
            await sqs.SetQueueAttributesAsync(new SetQueueAttributesRequest
            {
                QueueUrl = url,
                Attributes = new() { ["RedrivePolicy"] = RedrivePolicy(dlqArn, 4) },
            });
            expected.Add(url);
        }

        var (foreignUrl, _) = await Queue("foreign");
        await sqs.SetQueueAttributesAsync(new SetQueueAttributesRequest
        {
            QueueUrl = foreignUrl,
            Attributes = new() { ["RedrivePolicy"] = RedrivePolicy(otherDlqArn, 4) },
        });

        // The source listing is eventually consistent with SetQueueAttributes(RedrivePolicy).
        var deadline = Stopwatch.StartNew();
        HashSet<string> seen;
        while (true)
        {
            seen = [.. (await sqs.ListDeadLetterSourceQueuesAsync(new ListDeadLetterSourceQueuesRequest
            {
                QueueUrl = dlqUrl, MaxResults = 1000,
            })).QueueUrls ?? []];
            if (seen.SetEquals(expected) || deadline.Elapsed > TimeSpan.FromSeconds(90))
            {
                break;
            }

            await Task.Delay(TimeSpan.FromSeconds(2));
        }

        Ensure(seen.SetEquals(expected),
            $"an unpaged listing never converged on the {N} sources: {seen.Count} listed, " +
            $"{seen.Intersect(expected).Count()} of them expected, foreign queue " +
            $"{(seen.Contains(foreignUrl) ? "present" : "absent")}");

        var pages = await Walk(sqs, dlqUrl, 1);
        var sizes = pages.Select(p => p.Urls.Count).ToList();
        Ensure(sizes.SequenceEqual(Enumerable.Repeat(1, N)), $"MaxResults=1 over {N} sources gave page sizes [{string.Join(", ", sizes)}]");
        Ensure(pages[..^1].All(p => !string.IsNullOrEmpty(p.NextToken)) && pages[^1].NextToken is null,
            $"NextToken presence per page was [{string.Join(", ", pages.Select(p => !string.IsNullOrEmpty(p.NextToken)))}]; every page but the " +
            "last must carry one");
        var flat = pages.SelectMany(p => p.Urls).ToList();
        Ensure(flat.Distinct().Count() == flat.Count && flat.ToHashSet().SetEquals(expected),
            $"the paged walk returned {flat.Count} URLs ({flat.Distinct().Count()} distinct), " +
            $"{flat.ToHashSet().Intersect(expected).Count()} of the {N} expected sources");

        var other = (await sqs.ListDeadLetterSourceQueuesAsync(new ListDeadLetterSourceQueuesRequest { QueueUrl = otherDlqUrl })).QueueUrls ?? [];
        deadline = Stopwatch.StartNew();
        while (!other.SequenceEqual([foreignUrl]) && deadline.Elapsed < TimeSpan.FromSeconds(60))
        {
            await Task.Delay(TimeSpan.FromSeconds(2));
            other = (await sqs.ListDeadLetterSourceQueuesAsync(new ListDeadLetterSourceQueuesRequest { QueueUrl = otherDlqUrl })).QueueUrls ?? [];
        }

        Ensure(other.SequenceEqual([foreignUrl]),
            $"the second DLQ lists {other.Count} sources, expected only the one queue redriving to it");
    }

    private static async Task<List<(List<string> Urls, string? NextToken)>> Walk(AmazonSQSClient sqs, string dlqUrl, int pageSize)
    {
        List<(List<string> Urls, string? NextToken)> pages = [];
        var request = new ListDeadLetterSourceQueuesRequest { QueueUrl = dlqUrl, MaxResults = pageSize };
        for (var i = 0; i < 20; i++)
        {
            var resp = await sqs.ListDeadLetterSourceQueuesAsync(request);
            pages.Add((resp.QueueUrls ?? [], resp.NextToken));
            if (string.IsNullOrEmpty(resp.NextToken))
            {
                return pages;
            }

            request.NextToken = resp.NextToken;
        }

        throw Fail($"ListDeadLetterSourceQueues did not terminate after 20 pages: {pages.Count} pages");
    }

    /// <summary><c>json.dumps({"deadLetterTargetArn": arn, "maxReceiveCount": n})</c>, byte for byte.</summary>
    private static string RedrivePolicy(string deadLetterTargetArn, int maxReceiveCount) =>
        $"{{\"deadLetterTargetArn\": \"{deadLetterTargetArn}\", \"maxReceiveCount\": {maxReceiveCount}}}";
}
