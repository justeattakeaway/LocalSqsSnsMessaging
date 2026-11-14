// Translated from behavioral/sqs/check_list_queues_without_max_results_omits_next_token.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sqs;

/// <summary>sqs.list-queues-without-max-results-omits-next-token</summary>
[Category("AwsEvaluationSuite")]
public sealed class ListQueuesWithoutMaxResultsOmitsNextToken
{
    private const string Check = "list-queues-without-max-results-omits-next-token";

    /// <summary>
    /// ListQueues pages with a NextToken only when MaxResults is set: with MaxResults=1 two matching
    /// queues yield a NextToken, and with no MaxResults both urls come back with none.
    ///
    /// Reference: https://docs.aws.amazon.com/AWSSimpleQueueService/latest/APIReference/API_ListQueues.html
    /// </summary>
    [Test]
    [DisplayName("sqs.list-queues-without-max-results-omits-next-token")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sqs = ctx.Sqs;
        var prefix = FidelityContext.UniqueName("sim2real-behav-lqnt-");
        List<string> urls = [];
        foreach (var suffix in new[] { "-a", "-b" })
        {
            var url = (await sqs.CreateQueueAsync(new CreateQueueRequest { QueueName = prefix + suffix, Tags = new() { ["sim2real"] = Check } })).QueueUrl;
            ctx.OnCleanup(() => sqs.DeleteQueueAsync(url));
            urls.Add(url);
        }

        // ListQueues can lag CreateQueue; poll the unpaged, prefix-filtered listing.
        var timeoutS = BudgetS(ctx, 60);
        var deadline = FidelityTime.StartNew();
        var resp = await sqs.ListQueuesAsync(new ListQueuesRequest { QueueNamePrefix = prefix });
        while (!urls.ToHashSet().IsSubsetOf(resp.QueueUrls ?? []))
        {
            if (deadline.Elapsed.TotalSeconds >= timeoutS)
            {
                throw new TimeoutException(
                    $"ListQueues(prefix='{prefix}') did not list both [{string.Join(", ", urls)}] within " +
                    $"{timeoutS:F0}s; last answer [{string.Join(", ", resp.QueueUrls ?? [])}]");
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(1));
            resp = await sqs.ListQueuesAsync(new ListQueuesRequest { QueueNamePrefix = prefix });
        }

        Ensure(resp.QueueUrls!.Order(StringComparer.Ordinal).SequenceEqual(urls.Order(StringComparer.Ordinal)),
            $"ListQueues(prefix='{prefix}') with no MaxResults returned [{string.Join(", ", resp.QueueUrls!)}], " +
            $"expected exactly [{string.Join(", ", urls.Order(StringComparer.Ordinal))}]");
        Ensure(resp.NextToken is null,
            $"ListQueues with no MaxResults returned a NextToken: '{resp.NextToken}'");

        // Control: the same listing paged one url at a time does return a NextToken.
        var paged = await sqs.ListQueuesAsync(new ListQueuesRequest { QueueNamePrefix = prefix, MaxResults = 1 });
        Ensure((paged.QueueUrls ?? []).Count == 1 && urls.Contains(paged.QueueUrls![0]),
            $"ListQueues(MaxResults=1) returned [{string.Join(", ", paged.QueueUrls ?? [])}], expected one of [{string.Join(", ", urls)}]");
        Ensure(!string.IsNullOrEmpty(paged.NextToken),
            $"ListQueues(MaxResults=1) over two matching queues returned no NextToken: [{string.Join(", ", paged.QueueUrls ?? [])}]");
    }

    /// <summary>A wait's timeout, bounded by the runner's remaining time so the wait fails with its own message.</summary>
    private static double BudgetS(FidelityContext ctx, double capS)
    {
        var left = ctx.TimeLeftSeconds;
        return Math.Max(1.0, Math.Min(capS, left - 5.0));
    }
}
