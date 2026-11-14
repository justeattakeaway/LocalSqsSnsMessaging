// Translated from behavioral/sqs/check_purge_queue_discards_in_flight_messages.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Globalization;
using Amazon.SQS;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sqs;

/// <summary>sqs.purge-queue-discards-in-flight-messages</summary>
[Category("AwsEvaluationSuite")]
public sealed class PurgeQueueDiscardsInFlightMessages
{
    private const int N = 6;
    private const int InFlight = 3;

    // PurgeQueue can take up to 60s and forbids a second purge within that window, so the settle poll
    // is bounded well past it (a poll ceiling, not an asserted deadline) and the check purges once.
    private const int PurgeCeilingSeconds = 150;

    // The in-flight window must straddle the purge (60s) and expire inside the poll ceiling, so an
    // unpurged in-flight message is receivable again by the drain.
    private const int InflightVisibilitySeconds = 90;

    private static Dictionary<string, string> Tags => new() { ["sim2real:managed"] = "true", ["sim2real"] = "purge-queue-in-flight" };

    private static async Task<T> Poll<T>(Func<Task<T>> fn, Func<T, bool> pred, double timeoutSeconds, double intervalSeconds = 2.0)
    {
        var deadline = FidelityTime.StartNew();
        var val = await fn();
        while (!pred(val))
        {
            if (deadline.Elapsed >= TimeSpan.FromSeconds(timeoutSeconds))
            {
                return val;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(intervalSeconds));
            val = await fn();
        }

        return val;
    }

    private static async Task<(string Visible, string NotVisible)> Counts(AmazonSQSClient sqs, string url)
    {
        var a = (await sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest
        {
            QueueUrl = url,
            AttributeNames = ["ApproximateNumberOfMessages", "ApproximateNumberOfMessagesNotVisible"],
        })).Attributes!;
        return (a.GetValueOrDefault("ApproximateNumberOfMessages", "?"), a.GetValueOrDefault("ApproximateNumberOfMessagesNotVisible", "?"));
    }

    private static async Task<List<Message>> Drain(AmazonSQSClient sqs, string url, double timeoutSeconds, int waitSeconds = 5, int emptyPolls = 2)
    {
        var got = new List<Message>();
        var empties = 0;
        var deadline = FidelityTime.StartNew();
        while (empties < emptyPolls && deadline.Elapsed < TimeSpan.FromSeconds(timeoutSeconds))
        {
            var batch = (await sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = url, MaxNumberOfMessages = 10, WaitTimeSeconds = waitSeconds })).Messages ?? [];
            if (batch.Count == 0)
            {
                empties++;
                continue;
            }

            empties = 0;
            foreach (var m in batch)
            {
                got.Add(m);
                await sqs.DeleteMessageAsync(new DeleteMessageRequest { QueueUrl = url, ReceiptHandle = m.ReceiptHandle });
            }
        }

        return got;
    }

    /// <summary>
    /// PurgeQueue discards in-flight (received, undeleted) messages as well as visible ones: none
    /// reappear after their visibility timeout lapses, and both message counters return to 0.
    ///
    /// Reference: https://docs.aws.amazon.com/AWSSimpleQueueService/latest/APIReference/API_PurgeQueue.html
    /// </summary>
    [Test]
    [DisplayName("sqs.purge-queue-discards-in-flight-messages")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sqs = ctx.Sqs;
        var token = Guid.NewGuid().ToString("N")[..8];

        var url = (await sqs.CreateQueueAsync(new CreateQueueRequest
        {
            QueueName = FidelityContext.UniqueName("sim2real-behav-"),
            Attributes = new() { ["VisibilityTimeout"] = "5" },
            Tags = Tags,
        })).QueueUrl;
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(url));

        var bodies = Enumerable.Range(0, N).Select(i => $"purge-{token}-{i}").ToList();
        foreach (var b in bodies)
        {
            await sqs.SendMessageAsync(new SendMessageRequest { QueueUrl = url, MessageBody = b });
        }

        var nStr = N.ToString(CultureInfo.InvariantCulture);
        var visible = await Poll(async () => (await Counts(sqs, url)).Visible, v => v == nStr, timeoutSeconds: 60);
        Ensure(visible == nStr, $"only '{visible}' of {N} messages became visible, so the purge below has nothing to prove");

        // Put exactly _IN_FLIGHT messages in flight and do NOT delete them.
        var inflightBodies = new List<string>();
        var deadline = FidelityTime.StartNew();
        while (inflightBodies.Count < InFlight && deadline.Elapsed < TimeSpan.FromSeconds(60))
        {
            foreach (var m in (await sqs.ReceiveMessageAsync(new ReceiveMessageRequest
            {
                QueueUrl = url,
                MaxNumberOfMessages = InFlight - inflightBodies.Count,
                WaitTimeSeconds = 2,
                VisibilityTimeout = InflightVisibilitySeconds,
            })).Messages ?? [])
            {
                inflightBodies.Add(m.Body);
            }
        }

        Ensure(inflightBodies.Count == InFlight,
            $"could not put {InFlight} messages in flight, got {inflightBodies.Count} ([{string.Join(", ", inflightBodies)}])");

        var expectedCounts = ((N - InFlight).ToString(CultureInfo.InvariantCulture), InFlight.ToString(CultureInfo.InvariantCulture));
        var (preVisible, preNotVisible) = await Poll(() => Counts(sqs, url), c => c == expectedCounts, timeoutSeconds: 60);
        Ensure(preNotVisible == InFlight.ToString(CultureInfo.InvariantCulture),
            $"the {InFlight} received messages were never in flight, so the purge below proves " +
            $"nothing about in-flight messages: ApproximateNumberOfMessagesNotVisible=" +
            $"'{preNotVisible}' (visible='{preVisible}')");

        await sqs.PurgeQueueAsync(new PurgeQueueRequest { QueueUrl = url });

        // NotVisible reaches 0 whether the purge removed the in-flight set or its visibility lapsed;
        // the drain below tells the two apart.
        await Poll(async () => (await Counts(sqs, url)).NotVisible, v => v == "0", timeoutSeconds: PurgeCeilingSeconds);

        var drained = await Drain(sqs, url, timeoutSeconds: 60);
        var (postVisible, postNotVisible) = await Counts(sqs, url);
        var drainedBodies = drained.Select(m => m.Body).ToList();
        Ensure(drainedBodies.Count == 0, $"PurgeQueue left messages receivable: [{string.Join(", ", drainedBodies)}]");
        Ensure(!inflightBodies.Intersect(drainedBodies).Any(),
            $"an IN-FLIGHT message reappeared after PurgeQueue: " +
            $"[{string.Join(", ", inflightBodies.Intersect(drainedBodies).Order(StringComparer.Ordinal))}]");
        Ensure((postVisible, postNotVisible) == ("0", "0"),
            $"the message counters did not return to zero after the purge: " +
            $"ApproximateNumberOfMessages='{postVisible}' " +
            $"ApproximateNumberOfMessagesNotVisible='{postNotVisible}'");
    }
}
