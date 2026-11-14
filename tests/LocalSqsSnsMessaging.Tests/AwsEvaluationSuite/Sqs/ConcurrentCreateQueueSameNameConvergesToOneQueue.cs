// Translated from behavioral/sqs/check_concurrent_create_queue_same_name_converges_to_one_queue.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Diagnostics;
using Amazon.Runtime;
using Amazon.SQS;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sqs;

/// <summary>sqs.concurrent-create-queue-same-name-converges-to-one-queue</summary>
[Category("AwsEvaluationSuite")]
public sealed class ConcurrentCreateQueueSameNameConvergesToOneQueue
{
    private const int Creators = 10;
    private const double SettleS = 60.0;
    private static readonly Dictionary<string, string> Attrs = new() { ["VisibilityTimeout"] = "41", ["DelaySeconds"] = "0" };
    private static readonly Dictionary<string, string> Tags = new() { ["sim2real"] = "concurrent-create-queue-same-name-converges-to-one-queue" };
    private const string NoSuchQueue = "AWS.SimpleQueueService.NonExistentQueue";

    /// <summary>
    /// Ten simultaneous identical CreateQueue calls for one name all succeed with the same QueueUrl;
    /// the name lists once and the queue carries the requested attributes and tags.
    /// </summary>
    [Test]
    [DisplayName("sqs.concurrent-create-queue-same-name-converges-to-one-queue")]
    [Timeout(FidelityContext.BudgetMilliseconds)]
    [KnownFidelityGap(FidelityGap.Tags, "the raced queue carries tags {}, expected {'sim2real': 'concurrent-create-queue-same-name-converges-to-one-queue'}")]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sqs = ctx.Sqs;
        var name = FidelityContext.UniqueName("sim2real-behav-conc-create-");
        var barrier = new AsyncBarrier(Creators);

        async Task<object> Create(int _)
        {
            await barrier.Wait(TimeSpan.FromSeconds(30));
            try
            {
                return (await sqs.CreateQueueAsync(new CreateQueueRequest { QueueName = name, Attributes = new(Attrs), Tags = new(Tags) })).QueueUrl;
            }
            catch (AmazonServiceException exc)
            {
                return new AwsError(exc.ErrorCode, exc.Message, exc.StatusCode, exc);
            }
        }

        var results = await Task.WhenAll(Enumerable.Range(0, Creators).Select(i => Task.Run(() => Create(i))));
        var urls = results.OfType<string>().ToList();
        foreach (var u in urls.Distinct())
        {
            ctx.OnCleanup(() => DeleteQuietly(sqs, u));
        }

        var errors = results.Where(r => r is not string).ToList();
        Ensure(errors.Count == 0, $"{errors.Count} of {Creators} identical concurrent creates failed: [{string.Join(", ", errors)}]");
        Ensure(urls.Distinct().Count() == 1,
            $"{Creators} identical concurrent creates returned {urls.Distinct().Count()} distinct URLs");
        var url = urls[0];
        Ensure(url[(url.LastIndexOf('/') + 1)..] == name, $"the URL does not end in the queue name '{name}'");

        List<string> listed = [];
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(SettleS))
        {
            listed = await Listed(sqs, name);
            if (listed.Count > 0)
            {
                break;
            }

            await Task.Delay(TimeSpan.FromSeconds(3));
        }

        Ensure(listed.SequenceEqual([url]), $"ListQueues showed {listed.Count} queue(s) named '{name}', expected 1");

        var got = (await sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest
        {
            QueueUrl = url, AttributeNames = [.. Attrs.Keys.Order(StringComparer.Ordinal)],
        })).Attributes!;
        Ensure(DictEquals(got, Attrs), $"the raced queue carries {Show(got)}, expected {Show(Attrs)}");
        var tagsRead = (await sqs.ListQueueTagsAsync(new ListQueueTagsRequest { QueueUrl = url })).Tags ?? [];
        Ensure(DictEquals(tagsRead, Tags), $"the raced queue carries tags {Show(tagsRead)}, expected {Show(Tags)}");
    }

    private static bool DictEquals(Dictionary<string, string> a, Dictionary<string, string> b)
        => a.Count == b.Count && a.All(kv => b.TryGetValue(kv.Key, out var v) && v == kv.Value);

    private static string Show(Dictionary<string, string> d) => $"{{{string.Join(", ", d.Select(kv => $"'{kv.Key}': '{kv.Value}'"))}}}";

    private static async Task<List<string>> Listed(AmazonSQSClient sqs, string name)
    {
        var urls = new List<string>();
        string? nextToken = null;
        do
        {
            var page = await sqs.ListQueuesAsync(new ListQueuesRequest { QueueNamePrefix = name, NextToken = nextToken });
            urls.AddRange((page.QueueUrls ?? []).Where(u => u[(u.LastIndexOf('/') + 1)..] == name));
            nextToken = page.NextToken;
        }
        while (!string.IsNullOrEmpty(nextToken));

        return urls;
    }

    private static async Task DeleteQuietly(AmazonSQSClient sqs, string url)
    {
        try
        {
            await sqs.DeleteQueueAsync(new DeleteQueueRequest { QueueUrl = url });
        }
        catch (AmazonSQSException exc) when (exc.ErrorCode == NoSuchQueue)
        {
        }
    }

    /// <summary><c>threading.Barrier(parties)</c>; <c>Wait</c> times out like <c>Barrier.wait(timeout=...)</c>.</summary>
    private sealed class AsyncBarrier(int parties)
    {
        private readonly TaskCompletionSource _all = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _arrived;

        public async Task Wait(TimeSpan timeout)
        {
            if (Interlocked.Increment(ref _arrived) == parties)
            {
                _all.TrySetResult();
            }

            await _all.Task.WaitAsync(timeout);
        }
    }
}
