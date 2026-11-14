// Translated from behavioral/sqs/check_concurrent_fifo_dedup_race.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.Runtime;
using Amazon.SQS;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sqs;

/// <summary>sqs.concurrent-fifo-dedup-race</summary>
[Category("AwsEvaluationSuite")]
public sealed class ConcurrentFifoDedupRace
{
    private const string Check = "concurrent-fifo-dedup-race";

    /// <summary>
    /// N racing SendMessage calls sharing a MessageDeduplicationId collapse to exactly one delivered message.
    /// </summary>
    [Test]
    [DisplayName("sqs.concurrent-fifo-dedup-race")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sqs = ctx.Sqs;
        var url = (await sqs.CreateQueueAsync(new CreateQueueRequest
        {
            QueueName = FidelityContext.UniqueName("sim2real-behav-") + ".fifo",
            Attributes = new() { ["FifoQueue"] = "true" },
            Tags = new() { ["sim2real"] = Check },
        })).QueueUrl;
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(url));

        const string dedupId = "race-dedup-1";
        const string group = "race-group";
        const string body = "race-body";
        const int n = 6;

        async Task<object> Racer() => await sqs.SendMessageAsync(new SendMessageRequest
        {
            QueueUrl = url, MessageBody = body, MessageGroupId = group, MessageDeduplicationId = dedupId,
        });

        var results = await RunConcurrent(Enumerable.Repeat<Func<Task<object>>>(Racer, n).ToList(), timeoutS: 90);
        var successes = ConcurrentSuccesses(results);
        var codes = ConcurrentErrorCodes(results);
        Ensure(successes == n, $"racing sends raised: [{string.Join(", ", codes)}]");

        var messageIds = results.OfType<SendMessageResponse>().Select(r => r.MessageId).ToHashSet();
        Ensure(messageIds.Count == 1, $"dedup id produced {messageIds.Count} distinct message ids");

        var received = await ReceiveMessages(sqs, url, minCount: 1, attempts: 20);
        Ensure(received.Count == 1, $"expected exactly 1 deduplicated message, got {received.Count}");
    }

    private static async Task<object?[]> RunConcurrent(List<Func<Task<object>>> fns, double timeoutS = 60)
    {
        var tasks = fns.Select(fn => Task.Run(async () =>
        {
            try
            {
                return await fn();
            }
            catch (Exception exc)
            {
                return (object)exc;
            }
        })).ToList();
        await Task.WhenAny(Task.WhenAll(tasks), FidelityTime.Delay(TimeSpan.FromSeconds(timeoutS)));
        return [.. tasks.Select(t => t.IsCompletedSuccessfully ? t.Result : new TimeoutException())];
    }

    private static int ConcurrentSuccesses(object?[] results) => results.Count(r => r is not Exception);

    private static List<string?> ConcurrentErrorCodes(object?[] results)
    {
        var codes = new List<string?>();
        foreach (var r in results)
        {
            if (r is AmazonServiceException ce)
            {
                codes.Add(ce.ErrorCode);
            }
            else if (r is Exception e)
            {
                codes.Add(e.GetType().Name);
            }
        }

        return codes;
    }

    private static async Task<List<Message>> ReceiveMessages(AmazonSQSClient sqs, string url, int minCount, int maxBatch = 10, int attempts = 15)
    {
        var received = new List<Message>();
        var seen = new HashSet<string>();
        var idle = 0;
        for (var attempt = 0; attempt < attempts; attempt++)
        {
            var r = await sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = url, MaxNumberOfMessages = maxBatch, WaitTimeSeconds = 1 });
            var msgs = r.Messages ?? [];
            if (msgs.Count == 0)
            {
                idle++;
                if (received.Count > 0 && idle >= 3)
                {
                    break;
                }

                continue;
            }

            idle = 0;
            foreach (var m in msgs)
            {
                if (seen.Add(m.MessageId))
                {
                    received.Add(m);
                }
            }

            if (received.Count >= minCount)
            {
                break;
            }
        }

        return received;
    }
}
