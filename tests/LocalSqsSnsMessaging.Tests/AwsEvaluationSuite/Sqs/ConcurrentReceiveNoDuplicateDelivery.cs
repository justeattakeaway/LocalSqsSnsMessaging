// Translated from behavioral/sqs/check_concurrent_receive_no_duplicate_delivery.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Diagnostics;
using System.Globalization;
using Amazon.Runtime;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sqs;

/// <summary>sqs.concurrent-receive-no-duplicate-delivery</summary>
[Category("AwsEvaluationSuite")]
public sealed class ConcurrentReceiveNoDuplicateDelivery
{
    private const string Check = "concurrent-receive-no-duplicate-delivery";

    /// <summary>
    /// Concurrent ReceiveMessage calls on a FIFO queue (exactly-once processing) never hand out
    /// the same in-flight message to more than one caller.
    /// </summary>
    [Test]
    [DisplayName("sqs.concurrent-receive-no-duplicate-delivery")]
    [Timeout(FidelityContext.BudgetMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sqs = ctx.Sqs;
        var url = (await sqs.CreateQueueAsync(new CreateQueueRequest
        {
            QueueName = FidelityContext.UniqueName("sim2real-behav-") + ".fifo",
            Attributes = new() { ["VisibilityTimeout"] = "60", ["FifoQueue"] = "true" },
            Tags = new() { ["sim2real-check"] = Check },
        })).QueueUrl;
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(url));

        const int n = 20;
        for (var i = 0; i < n; i++)
        {
            // One message group per message, so every message is receivable concurrently.
            await sqs.SendMessageAsync(new SendMessageRequest { QueueUrl = url, MessageBody = $"m{i}", MessageGroupId = $"g{i}", MessageDeduplicationId = $"d{i}" });
        }

        // ApproximateNumberOfMessages is eventually consistent on real AWS; let it settle first.
        await Poll(
            async () => int.Parse((await sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest
            {
                QueueUrl = url, AttributeNames = ["ApproximateNumberOfMessages"],
            })).Attributes!.GetValueOrDefault("ApproximateNumberOfMessages", "0"), CultureInfo.InvariantCulture),
            c => c >= n,
            timeoutS: 30,
            intervalS: 2);

        async Task<object> Receiver() => (await sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = url, MaxNumberOfMessages = 10, WaitTimeSeconds = 2 })).Messages ?? [];

        var results = await RunConcurrent(Enumerable.Repeat<Func<Task<object>>>(Receiver, 6).ToList(), timeoutS: 90);
        var errs = ConcurrentErrorCodes(results);
        Ensure(errs.Count == 0, $"concurrent receives raised: [{string.Join(", ", errs)}]");

        var allMsgs = results.OfType<List<Message>>().SelectMany(b => b).ToList();
        var ids = allMsgs.Select(m => m.MessageId).ToList();

        Ensure(ids.Count == ids.Distinct().Count(), "the same message was concurrently delivered to more than one receiver");
        Ensure(ids.Count <= n, $"received more messages ({ids.Count}) than were sent ({n})");
        Ensure(ids.Count > 0, "no messages were delivered at all");
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
        await Task.WhenAny(Task.WhenAll(tasks), Task.Delay(TimeSpan.FromSeconds(timeoutS)));
        return [.. tasks.Select(t => t.IsCompletedSuccessfully ? t.Result : new TimeoutException())];
    }

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

    private static async Task<T> Poll<T>(Func<Task<T>> fn, Func<T, bool> pred, double timeoutS, double intervalS)
    {
        var deadline = Stopwatch.StartNew();
        var val = await fn();
        while (!pred(val))
        {
            if (deadline.Elapsed >= TimeSpan.FromSeconds(timeoutS))
            {
                return val;
            }

            await Task.Delay(TimeSpan.FromSeconds(intervalS));
            val = await fn();
        }

        return val;
    }
}
