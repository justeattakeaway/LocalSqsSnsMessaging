// Translated from behavioral/sqs/check_approximate_counters_converge_through_receive_release_and_delete.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Diagnostics;
using System.Globalization;
using Amazon.SQS;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sqs;

/// <summary>sqs.approximate-counters-converge-through-receive-release-and-delete</summary>
[Category("AwsEvaluationSuite")]
public sealed class ApproximateCountersConvergeThroughReceiveReleaseAndDelete
{
    private static readonly Dictionary<string, string> Tags = new() { ["sim2real:managed"] = "true", ["sim2real"] = "approx-counter-transitions" };

    // The GetQueueAttributes reference allows the approximate counters about a minute to settle.
    private const double ConvergeS = 120.0;
    private static readonly string[] Counters = ["ApproximateNumberOfMessages", "ApproximateNumberOfMessagesNotVisible", "ApproximateNumberOfMessagesDelayed"];

    /// <summary>
    /// The (visible, in-flight, delayed) approximate counters converge after each change: five sent
    /// (5, 0, 0), three received (2, 3, 0), one released (3, 2, 0), one deleted (3, 1, 0), and the
    /// rest drained (0, 0, 0).
    /// </summary>
    [Test]
    [DisplayName("sqs.approximate-counters-converge-through-receive-release-and-delete")]
    [Timeout(FidelityContext.BudgetMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sqs = ctx.Sqs;
        var token = Guid.NewGuid().ToString("N")[..8];
        var url = (await sqs.CreateQueueAsync(new CreateQueueRequest
        {
            QueueName = FidelityContext.UniqueName("sim2real-behav-"),
            Attributes = new() { ["VisibilityTimeout"] = "600" },
            Tags = new(Tags),
        })).QueueUrl;
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(url));
        await Converge(sqs, url, (0, 0, 0), "fresh queue");

        var bodies = Enumerable.Range(0, 5).Select(i => $"count-{i}-{token}").ToList();
        await sqs.SendMessageBatchAsync(new SendMessageBatchRequest
        {
            QueueUrl = url,
            Entries = bodies.Select((b, i) => new SendMessageBatchRequestEntry { Id = i.ToString(CultureInfo.InvariantCulture), MessageBody = b }).ToList(),
        });
        await Converge(sqs, url, (5, 0, 0), "after sending five");

        var held = new List<Message>();
        var deadline = Stopwatch.StartNew();
        while (held.Count < 3 && deadline.Elapsed < TimeSpan.FromSeconds(60))
        {
            held.AddRange((await sqs.ReceiveMessageAsync(new ReceiveMessageRequest
            {
                QueueUrl = url, MaxNumberOfMessages = 3 - held.Count, WaitTimeSeconds = 2,
            })).Messages ?? []);
        }

        Ensure(held.Count == 3, $"expected to take three messages in flight, got {held.Count}");
        await Converge(sqs, url, (2, 3, 0), "after taking three in flight");

        await sqs.ChangeMessageVisibilityAsync(new ChangeMessageVisibilityRequest { QueueUrl = url, ReceiptHandle = held[0].ReceiptHandle, VisibilityTimeout = 0 });
        await Converge(sqs, url, (3, 2, 0), "after releasing one with VisibilityTimeout=0");

        await sqs.DeleteMessageAsync(new DeleteMessageRequest { QueueUrl = url, ReceiptHandle = held[1].ReceiptHandle });
        await Converge(sqs, url, (3, 1, 0), "after deleting one in-flight message");

        await sqs.DeleteMessageAsync(new DeleteMessageRequest { QueueUrl = url, ReceiptHandle = held[2].ReceiptHandle });
        var seen = new HashSet<string> { held[1].Body, held[2].Body };
        deadline = Stopwatch.StartNew();
        while (seen.Count < 5 && deadline.Elapsed < TimeSpan.FromSeconds(60))
        {
            foreach (var m in (await sqs.ReceiveMessageAsync(new ReceiveMessageRequest
            {
                QueueUrl = url, MaxNumberOfMessages = 10, WaitTimeSeconds = 2,
            })).Messages ?? [])
            {
                seen.Add(m.Body);
                await sqs.DeleteMessageAsync(new DeleteMessageRequest { QueueUrl = url, ReceiptHandle = m.ReceiptHandle });
            }
        }

        Ensure(seen.SetEquals(bodies), $"drain saw [{string.Join(", ", seen.Order(StringComparer.Ordinal))}], expected [{string.Join(", ", bodies.Order(StringComparer.Ordinal))}]");
        await Converge(sqs, url, (0, 0, 0), "after deleting everything");
    }

    private static async Task<(int, int, int)> Read(AmazonSQSClient sqs, string url)
    {
        var a = (await sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest { QueueUrl = url, AttributeNames = [.. Counters] })).Attributes!;
        var v = Counters.Select(k => int.Parse(a[k], CultureInfo.InvariantCulture)).ToArray();
        return (v[0], v[1], v[2]);
    }

    private static async Task Converge(AmazonSQSClient sqs, string url, (int, int, int) want, string stage)
    {
        var got = await Read(sqs, url);
        var deadline = Stopwatch.StartNew();
        while (got != want && deadline.Elapsed < TimeSpan.FromSeconds(ConvergeS))
        {
            await Task.Delay(TimeSpan.FromSeconds(3));
            got = await Read(sqs, url);
        }

        Ensure(got == want,
            $"{stage}: (visible, in-flight, delayed) did not converge to {want} within {ConvergeS:F0}s; last read {got}");
    }
}
