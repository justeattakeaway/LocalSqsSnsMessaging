// Translated from behavioral/sqs/check_visibility_timeout_shorten_and_receive_override.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Diagnostics;
using System.Globalization;
using Amazon.SQS;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sqs;

/// <summary>sqs.visibility-timeout-shorten-and-receive-override</summary>
[Category("AwsEvaluationSuite")]
public sealed class VisibilityTimeoutShortenAndReceiveOverride
{
    private const string Check = "visibility-timeout-shorten-and-receive-override";

    // Queue default kept at 1s so the control leg costs a second. The 30s override is never waited
    // out: it only has to outlast the 5s hidden-window probe to prove it beat the default.
    private const int QueueDefaultVt = 1;
    private const int ReceiveOverrideVt = 30;
    private const int ShortenedVt = 2;

    /// <summary>
    /// A per-request ReceiveMessage VisibilityTimeout overrides the queue default, and
    /// ChangeMessageVisibility can shorten an in-flight message's timeout to a non-zero value,
    /// after which it reappears on the new schedule rather than instantly.
    /// </summary>
    // timing:real-time — visibility: 2 s https://docs.aws.amazon.com/AWSSimpleQueueService/latest/SQSDeveloperGuide/sqs-visibility-timeout.html
    [Test]
    [DisplayName("sqs.visibility-timeout-shorten-and-receive-override")]
    [Timeout(FidelityContext.RealTimeBudgetMilliseconds)]
    [Category(ApplyDefaultCategoryAttribute.TimeBasedCategoryName)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.RealTimeBudgetMilliseconds, cancellationToken);
        var sqs = ctx.Sqs;
        var url = (await sqs.CreateQueueAsync(new CreateQueueRequest
        {
            QueueName = FidelityContext.UniqueName("sim2real-behav-"),
            Attributes = new() { ["VisibilityTimeout"] = QueueDefaultVt.ToString(CultureInfo.InvariantCulture) },
            Tags = new() { ["sim2real-check"] = Check },
        })).QueueUrl;
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(url));

        var attrs = await sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest { QueueUrl = url, AttributeNames = ["VisibilityTimeout"] });
        Ensure(attrs.Attributes!["VisibilityTimeout"] == QueueDefaultVt.ToString(CultureInfo.InvariantCulture),
            $"queue default did not stick: '{attrs.Attributes.GetValueOrDefault("VisibilityTimeout")}'");

        var token = Guid.NewGuid().ToString("N")[..8];
        var body = $"vt-override-{token}";
        await sqs.SendMessageAsync(new SendMessageRequest { QueueUrl = url, MessageBody = body });

        // The very first receive carries the override, so the hidden-window probe below is
        // measuring the override and nothing else.
        var taken = await Poll(
            async () => (await sqs.ReceiveMessageAsync(new ReceiveMessageRequest
            {
                QueueUrl = url,
                MaxNumberOfMessages = 1,
                WaitTimeSeconds = 1,
                VisibilityTimeout = ReceiveOverrideVt,
            })).Messages ?? [],
            msgs => msgs.Count > 0,
            timeoutSeconds: 20,
            intervalSeconds: 0);
        Ensure(taken.Count > 0, $"could not receive {body} with a per-request VisibilityTimeout");
        var handle = taken[0].ReceiptHandle;
        Ensure(taken[0].Body == body, $"received '{taken[0].Body}', expected '{body}'");

        // --- the override beat the 1s queue default: still hidden well past 1s -------------
        const int hiddenProbeS = 5;
        var (elapsed, leaked) = await AwaitVisible(sqs, url, timeoutSeconds: hiddenProbeS);
        Ensure(leaked is null,
            $"message became visible after {elapsed:F1}s despite a per-request " +
            $"VisibilityTimeout={ReceiveOverrideVt}s -- the queue default of " +
            $"{QueueDefaultVt}s was applied instead of the request override");

        // In-flight is observable in the approximate counters (direction only, never an exact read).
        var notvis = await Poll(
            async () => (await sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest
            {
                QueueUrl = url,
                AttributeNames = ["ApproximateNumberOfMessagesNotVisible"],
            })).Attributes!.GetValueOrDefault("ApproximateNumberOfMessagesNotVisible", "0"),
            v => v != "0",
            timeoutSeconds: 20);
        Ensure(int.Parse(notvis, CultureInfo.InvariantCulture) >= 1,
            $"ApproximateNumberOfMessagesNotVisible stayed at '{notvis}' while a message was in " +
            "flight under a 30s timeout");

        // --- shorten to a NON-ZERO timeout: it reappears, but not instantly ----------------
        await sqs.ChangeMessageVisibilityAsync(new ChangeMessageVisibilityRequest { QueueUrl = url, ReceiptHandle = handle, VisibilityTimeout = ShortenedVt });
        (elapsed, var again) = await AwaitVisible(sqs, url, timeoutSeconds: 25);
        Ensure(again is not null,
            $"ChangeMessageVisibility({ShortenedVt}) did not shorten the remaining " +
            $"{ReceiveOverrideVt}s timeout: the message was still hidden {elapsed:F1}s later");
        Ensure(again.Body == body, $"reappeared with body '{again.Body}'");
        Ensure(elapsed >= 1.0,
            $"message reappeared after only {elapsed:F2}s -- ChangeMessageVisibility" +
            $"({ShortenedVt}) was treated as 0 rather than as a {ShortenedVt}s timeout");
        await sqs.DeleteMessageAsync(new DeleteMessageRequest { QueueUrl = url, ReceiptHandle = again.ReceiptHandle });

        // --- control: with no per-request override, the 1s queue default applies -----------
        var ctlBody = $"vt-default-{token}";
        await sqs.SendMessageAsync(new SendMessageRequest { QueueUrl = url, MessageBody = ctlBody });
        var ctl = await Poll(
            async () => (await sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = url, MaxNumberOfMessages = 1, WaitTimeSeconds = 1 })).Messages ?? [],
            msgs => msgs.Count > 0,
            timeoutSeconds: 20,
            intervalSeconds: 0);
        Ensure(ctl.Count > 0 && ctl[0].Body == ctlBody, $"control message not received: [{string.Join(", ", ctl.Select(m => m.Body))}]");
        var (ctlElapsed, ctlAgain) = await AwaitVisible(sqs, url, timeoutSeconds: 15);
        Ensure(ctlAgain is not null,
            $"a message received with NO override stayed hidden for {ctlElapsed:F1}s; with a " +
            $"{QueueDefaultVt}s queue default it should have returned promptly -- the override " +
            "from the earlier receive appears to have been persisted onto the queue");
        Ensure(ctlAgain.Body == ctlBody, $"control reappeared as '{ctlAgain.Body}'");
        await sqs.DeleteMessageAsync(new DeleteMessageRequest { QueueUrl = url, ReceiptHandle = ctlAgain.ReceiptHandle });
    }

    /// <summary>
    /// Poll without long-poll until a message is visible.
    ///
    /// Returns (elapsed_seconds, message_or_None).
    /// </summary>
    private static async Task<(double Elapsed, Message? Message)> AwaitVisible(AmazonSQSClient sqs, string url, double timeoutSeconds)
    {
        var start = Stopwatch.StartNew();
        while (true)
        {
            var msgs = (await sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = url, MaxNumberOfMessages = 1, WaitTimeSeconds = 0 })).Messages ?? [];
            if (msgs.Count > 0)
            {
                return (start.Elapsed.TotalSeconds, msgs[0]);
            }

            if (start.Elapsed >= TimeSpan.FromSeconds(timeoutSeconds))
            {
                return (start.Elapsed.TotalSeconds, null);
            }

            await Task.Delay(TimeSpan.FromSeconds(0.25));
        }
    }

    private static async Task<T> Poll<T>(Func<Task<T>> fn, Func<T, bool> pred, double timeoutSeconds, double intervalSeconds = 0.5)
    {
        var deadline = Stopwatch.StartNew();
        var val = await fn();
        while (!pred(val))
        {
            if (deadline.Elapsed >= TimeSpan.FromSeconds(timeoutSeconds))
            {
                return val;
            }

            await Task.Delay(TimeSpan.FromSeconds(intervalSeconds));
            val = await fn();
        }

        return val;
    }
}
