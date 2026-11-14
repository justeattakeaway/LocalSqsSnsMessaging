// Translated from behavioral/sqs/check_concurrent_set_queue_attributes_on_distinct_attributes_lose_no_update.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Diagnostics;
using Amazon.SQS;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sqs;

/// <summary>sqs.concurrent-set-queue-attributes-on-distinct-attributes-lose-no-update</summary>
[Category("AwsEvaluationSuite")]
public sealed class ConcurrentSetQueueAttributesOnDistinctAttributesLoseNoUpdate
{
    private const double SettleS = 75.0;
    private static readonly Dictionary<string, string> Tags = new() { ["sim2real"] = "concurrent-set-queue-attributes-on-distinct-attributes-lose-no-update" };

    private static readonly Dictionary<string, string>[] Rounds =
    [
        new()
        {
            ["VisibilityTimeout"] = "77", ["DelaySeconds"] = "7", ["MaximumMessageSize"] = "200000",
            ["MessageRetentionPeriod"] = "7777", ["ReceiveMessageWaitTimeSeconds"] = "7",
        },
        new()
        {
            ["VisibilityTimeout"] = "88", ["DelaySeconds"] = "8", ["MaximumMessageSize"] = "300000",
            ["MessageRetentionPeriod"] = "8888", ["ReceiveMessageWaitTimeSeconds"] = "8",
        },
    ];

    /// <summary>
    /// Five simultaneous SetQueueAttributes calls on distinct attributes all take effect, over two
    /// rounds: no concurrent write overwrites another's attribute with a stale value.
    /// </summary>
    [Test]
    [DisplayName("sqs.concurrent-set-queue-attributes-on-distinct-attributes-lose-no-update")]
    [Timeout(FidelityContext.BudgetMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sqs = ctx.Sqs;
        var url = (await sqs.CreateQueueAsync(new CreateQueueRequest
        {
            QueueName = FidelityContext.UniqueName("sim2real-behav-conc-attrs-"), Tags = new(Tags),
        })).QueueUrl;
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(url));

        foreach (var want in Rounds)
        {
            var barrier = new AsyncBarrier(want.Count);

            async Task Write(KeyValuePair<string, string> item)
            {
                await barrier.Wait(TimeSpan.FromSeconds(30));
                await sqs.SetQueueAttributesAsync(new SetQueueAttributesRequest { QueueUrl = url, Attributes = new() { [item.Key] = item.Value } });
            }

            await Task.WhenAll(want.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(item => Task.Run(() => Write(item))));

            var got = await Converge(sqs, url, want);
            var lost = want.Where(kv => got.GetValueOrDefault(kv.Key) != kv.Value)
                .ToDictionary(kv => kv.Key, kv => (kv.Value, got.GetValueOrDefault(kv.Key)));
            Ensure(lost.Count == 0,
                $"{lost.Count} of {want.Count} concurrent single-attribute writes did not survive " +
                $"(attribute: (written, read back)) {{{string.Join(", ", lost.Select(kv => $"{kv.Key}: {kv.Value}"))}}} within {SettleS}s");
        }
    }

    private static async Task<Dictionary<string, string>> Converge(AmazonSQSClient sqs, string url, Dictionary<string, string> want)
    {
        var got = new Dictionary<string, string>();
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(SettleS))
        {
            got = (await sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest
            {
                QueueUrl = url, AttributeNames = [.. want.Keys.Order(StringComparer.Ordinal)],
            })).Attributes!;
            if (got.Count == want.Count && got.All(kv => want.TryGetValue(kv.Key, out var v) && v == kv.Value))
            {
                return got;
            }

            await Task.Delay(TimeSpan.FromSeconds(3));
        }

        return got;
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
