// Translated from behavioral/sqs/check_long_poll_wakes_when_delay_or_visibility_expires.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Diagnostics;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sqs;

/// <summary>sqs.long-poll-wakes-when-delay-or-visibility-expires</summary>
[Category("AwsEvaluationSuite")]
public sealed class LongPollWakesWhenDelayOrVisibilityExpires
{
    private const int DelayS = 4;
    private const int VisibilityS = 4;
    private const int WaitS = 20;

    /// <summary>
    /// A single long poll started while the only message is delayed, or in flight, returns it within
    /// the same call once the delay or visibility timeout expires (redelivery has a new handle).
    /// </summary>
    [Test]
    [DisplayName("sqs.long-poll-wakes-when-delay-or-visibility-expires")]
    [Timeout(FidelityContext.BudgetMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sqs = ctx.Sqs;
        var url = (await sqs.CreateQueueAsync(new CreateQueueRequest { QueueName = FidelityContext.UniqueName("sim2real-behav-lpwake-") })).QueueUrl;
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(url));

        // Leg 1: delay expiry. Nothing else is on the queue, so the one call below can only return
        // this message, and only after its DelaySeconds has run out.
        await sqs.SendMessageAsync(new SendMessageRequest { QueueUrl = url, MessageBody = "delayed", DelaySeconds = DelayS });
        var woke = (await sqs.ReceiveMessageAsync(new ReceiveMessageRequest
        {
            QueueUrl = url, MaxNumberOfMessages = 1, WaitTimeSeconds = WaitS,
        })).Messages ?? [];
        Ensure(woke.Select(m => m.Body).SequenceEqual(["delayed"]),
            $"a {WaitS}s long poll started inside a {DelayS}s message delay must return the " +
            $"message once the delay expires; it returned [{string.Join(", ", woke.Select(m => m.Body))}]");
        await sqs.DeleteMessageAsync(new DeleteMessageRequest { QueueUrl = url, ReceiptHandle = woke[0].ReceiptHandle });

        // Leg 2: visibility expiry. Receive the only message with a short visibility timeout, leave
        // it unacknowledged, and long-poll again while it is in flight.
        await sqs.SendMessageAsync(new SendMessageRequest { QueueUrl = url, MessageBody = "in-flight" });
        Message? first = null;
        var deadline = Stopwatch.StartNew();
        while (first is null && deadline.Elapsed < TimeSpan.FromSeconds(30))
        {
            var msgs = (await sqs.ReceiveMessageAsync(new ReceiveMessageRequest
            {
                QueueUrl = url, MaxNumberOfMessages = 1, WaitTimeSeconds = 2, VisibilityTimeout = VisibilityS,
            })).Messages ?? [];
            first = msgs.Count > 0 ? msgs[0] : null;
        }

        Ensure(first is not null && first.Body == "in-flight",
            $"the second message was never delivered: {first?.Body ?? "None"}");
        var again = (await sqs.ReceiveMessageAsync(new ReceiveMessageRequest
        {
            QueueUrl = url, MaxNumberOfMessages = 1, WaitTimeSeconds = WaitS,
            MessageSystemAttributeNames = ["ApproximateReceiveCount"],
        })).Messages ?? [];
        Ensure(again.Select(m => m.Body).SequenceEqual(["in-flight"]),
            $"a {WaitS}s long poll started while the only message was in flight for " +
            $"{VisibilityS}s must return it once the visibility timeout expires; it returned " +
            $"[{string.Join(", ", again.Select(m => m.Body))}]");
        var count = again[0].Attributes!.GetValueOrDefault("ApproximateReceiveCount");
        Ensure(count == "2", $"the redelivery must be the message's second receive; count was '{count}'");
        Ensure(again[0].ReceiptHandle != first!.ReceiptHandle,
            "the redelivery inside the long poll must carry a new receipt handle");
        await sqs.DeleteMessageAsync(new DeleteMessageRequest { QueueUrl = url, ReceiptHandle = again[0].ReceiptHandle });
    }
}
