// Translated from behavioral/sqs/check_retention_expiry_discards_an_in_flight_message.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Diagnostics;
using Amazon.SQS;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sqs;

/// <summary>sqs.retention-expiry-discards-an-in-flight-message</summary>
[Category("AwsEvaluationSuite")]
public sealed class RetentionExpiryDiscardsAnInFlightMessage
{
    private const int RetentionS = 60;
    // Longer than the retention period: the message is still in flight when it expires.
    private const int VtS = 90;
    private const double CounterS = 120.0;

    private static Dictionary<string, string> Tags() => new() { ["sim2real:managed"] = "true", ["sim2real"] = "retention-in-flight" };

    /// <summary>
    /// Retention runs from the send: a message received under a visibility timeout longer than
    /// MessageRetentionPeriod expires while in flight and never returns, while a twin on a
    /// default-retention queue reappears once its timeout lapses.
    /// </summary>
    // timing:real-time — retention: 60 s https://docs.aws.amazon.com/AWSSimpleQueueService/latest/APIReference/API_SetQueueAttributes.html
    [Test]
    [DisplayName("sqs.retention-expiry-discards-an-in-flight-message")]
    [Timeout(FidelityContext.RealTimeBudgetMilliseconds)]
    [Category(ApplyDefaultCategoryAttribute.TimeBasedCategoryName)]
    [Category(FidelityContext.LongWaitCategory)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.RealTimeBudgetMilliseconds, cancellationToken);
        var sqs = ctx.Sqs;
        var clock = Stopwatch.StartNew(); // time.monotonic()
        double Now() => clock.Elapsed.TotalSeconds;
        var token = Guid.NewGuid().ToString("N")[..8];
        var shortUrl = (await sqs.CreateQueueAsync(new CreateQueueRequest
        {
            QueueName = FidelityContext.UniqueName("sim2real-behav-") + "-short",
            Attributes = new() { ["MessageRetentionPeriod"] = RetentionS.ToString(), ["VisibilityTimeout"] = VtS.ToString() },
            Tags = Tags(),
        })).QueueUrl;
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(shortUrl));
        var twinUrl = (await sqs.CreateQueueAsync(new CreateQueueRequest
        {
            QueueName = FidelityContext.UniqueName("sim2real-behav-") + "-twin",
            Attributes = new() { ["VisibilityTimeout"] = VtS.ToString() },
            Tags = Tags(),
        })).QueueUrl;
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(twinUrl));

        var got = (await sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest
        {
            QueueUrl = shortUrl,
            AttributeNames = ["MessageRetentionPeriod"],
        })).Attributes!["MessageRetentionPeriod"];
        Ensure(got == RetentionS.ToString(), $"short queue retention read back '{got}'");

        var (shortBody, twinBody) = ($"short-{token}", $"twin-{token}");
        await sqs.SendMessageAsync(new SendMessageRequest { QueueUrl = shortUrl, MessageBody = shortBody });
        await sqs.SendMessageAsync(new SendMessageRequest { QueueUrl = twinUrl, MessageBody = twinBody });
        Dictionary<string, Message> held = [];
        var deadline = Now() + 30;
        while (held.Count < 2 && Now() < deadline)
        {
            foreach (var url in new[] { shortUrl, twinUrl })
            {
                if (!held.ContainsKey(url))
                {
                    var ms = await Receive(sqs, url);
                    if (ms.Count > 0)
                    {
                        held[url] = ms[0];
                    }
                }
            }
        }

        Ensure(held.Count == 2 && held.GetValueOrDefault(shortUrl)?.Body == shortBody && held.GetValueOrDefault(twinUrl)?.Body == twinBody,
            $"expected to take both messages in flight, got [{string.Join(", ", held.Values.Select(m => m.Body))}]");
        var receivedAt = Now();

        // --- after the visibility timeout: the twin is back, the expired one is not -----------
        List<Message> twinBack = [];
        deadline = receivedAt + VtS + 60;
        while (twinBack.Count == 0 && Now() < deadline)
        {
            twinBack = await Receive(sqs, twinUrl, waitS: 5);
        }

        Ensure(twinBack.Select(m => m.Body).SequenceEqual([twinBody]),
            $"the default-retention twin did not reappear after its {VtS}s timeout: "
            + $"[{string.Join(", ", twinBack.Select(m => m.Body))}]");
        await sqs.DeleteMessageAsync(new DeleteMessageRequest { QueueUrl = twinUrl, ReceiptHandle = twinBack[0].ReceiptHandle });

        // The same window again for the short-retention queue, now past both deadlines.
        List<string> late = [];
        var settle = Now() + 20;
        while (Now() < settle)
        {
            late.AddRange((await Receive(sqs, shortUrl, waitS: 5)).Select(m => m.Body));
        }

        Ensure(late.Count == 0,
            $"a message older than MessageRetentionPeriod={RetentionS}s came back after its "
            + $"{VtS}s visibility timeout; it expired while in flight: [{string.Join(", ", late)}]");

        var counters = await Counters(sqs, shortUrl);
        deadline = Now() + CounterS;
        while (counters != ("0", "0") && Now() < deadline)
        {
            await Task.Delay(TimeSpan.FromSeconds(5));
            counters = await Counters(sqs, shortUrl);
        }

        Ensure(counters == ("0", "0"),
            $"the expired queue's (visible, in-flight) counters must converge to 0: {counters}");
    }

    private static async Task<List<Message>> Receive(IAmazonSQS sqs, string url, int waitS = 2) =>
        (await sqs.ReceiveMessageAsync(new ReceiveMessageRequest
        {
            QueueUrl = url,
            MaxNumberOfMessages = 10,
            WaitTimeSeconds = waitS,
        })).Messages ?? [];

    private static async Task<(string Visible, string InFlight)> Counters(IAmazonSQS sqs, string url)
    {
        var a = (await sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest
        {
            QueueUrl = url,
            AttributeNames = ["ApproximateNumberOfMessages", "ApproximateNumberOfMessagesNotVisible"],
        })).Attributes!;
        return (a["ApproximateNumberOfMessages"], a["ApproximateNumberOfMessagesNotVisible"]);
    }
}
