// Translated from behavioral/sqs/check_list_queues_prefix_matches_only_at_start.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.SQS;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sqs;

/// <summary>sqs.list-queues-prefix-matches-only-at-start</summary>
[Category("AwsEvaluationSuite")]
public sealed class ListQueuesPrefixMatchesOnlyAtStart
{
    private static readonly Dictionary<string, string> Tags = new()
    {
        ["sim2real"] = "list-queues-prefix-matches-only-at-start",
    };

    /// <summary>
    /// ListQueues(QueueNamePrefix) matches only at the start of the name: a queue whose name merely
    /// contains the prefix string is excluded, while one whose name starts with it is included.
    /// </summary>
    [Test]
    [DisplayName("sqs.list-queues-prefix-matches-only-at-start")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sqs = ctx.Sqs;
        var token = FidelityContext.UniqueName("sim2real-behav-anchor-");

        // Starts with the token: must match.
        var startsName = token + "-head";
        var startsUrl = (await sqs.CreateQueueAsync(new CreateQueueRequest { QueueName = startsName, Tags = new(Tags) })).QueueUrl;
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(startsUrl));

        // Contains the token, but does not start with it: must NOT match.
        var midName = FidelityContext.UniqueName("sim2real-behav-pre-") + "-" + token;
        var midUrl = (await sqs.CreateQueueAsync(new CreateQueueRequest { QueueName = midName, Tags = new(Tags) })).QueueUrl;
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(midUrl));

        var deadline = FidelityTime.StartNew();
        List<string> urls;
        while (true)
        {
            urls = await ListUrls(sqs, token);
            if (urls.Contains(startsUrl))
            {
                break;
            }

            Ensure(deadline.Elapsed < TimeSpan.FromSeconds(30),
                $"ListQueues(prefix='{token}') never returned the prefix-matching queue '{startsUrl}'");
            await FidelityTime.Delay(TimeSpan.FromSeconds(1));
        }

        Ensure(!urls.Contains(midUrl),
            $"ListQueues(prefix='{token}') returned '{midUrl}', whose name '{midName}' contains " +
            "the filter but does not start with it — the filter is matching as a substring, not a prefix");
    }

    private static async Task<List<string>> ListUrls(AmazonSQSClient sqs, string prefix)
    {
        List<string> urls = [];
        string? token = null;
        while (true)
        {
            var request = new ListQueuesRequest { QueueNamePrefix = prefix };
            if (!string.IsNullOrEmpty(token))
            {
                request.NextToken = token;
            }

            var page = await sqs.ListQueuesAsync(request);
            urls.AddRange(page.QueueUrls ?? []);
            token = page.NextToken;
            if (string.IsNullOrEmpty(token))
            {
                return urls;
            }
        }
    }
}
