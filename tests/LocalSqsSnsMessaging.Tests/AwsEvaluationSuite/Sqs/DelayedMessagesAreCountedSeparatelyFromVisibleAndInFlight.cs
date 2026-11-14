// Translated from behavioral/sqs/check_delayed_messages_are_counted_separately_from_visible_and_in_flight.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Diagnostics;
using Amazon.SQS;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sqs;

/// <summary>sqs.delayed-messages-are-counted-separately-from-visible-and-in-flight</summary>
[Category("AwsEvaluationSuite")]
public sealed class DelayedMessagesAreCountedSeparatelyFromVisibleAndInFlight
{
    // 300s so the documented >=1 minute counter-consistency window closes long before the delay
    // does. The check never waits this delay out.
    private const int LongDelaySeconds = 300;
    // Short enough to wait out inside the budget, long enough to be many multiples of the measured
    // undelayed-delivery latency L.
    private const int ShortDelaySeconds = 15;
    private const double CounterSeconds = 120.0;
    private const double PurgeSeconds = 90.0;
    private const int AbsenceMultiplier = 3; // hold absence for >=3x the latency measured on the same queue

    private static readonly string[] Counters =
    [
        "ApproximateNumberOfMessages",
        "ApproximateNumberOfMessagesNotVisible",
        "ApproximateNumberOfMessagesDelayed",
    ];

    private static readonly Dictionary<string, string> Tags = new()
    {
        ["sim2real:managed"] = "true",
        ["sim2real"] = "delayed-message-accounting",
    };

    // timing:real-time — visibility: 15 s https://docs.aws.amazon.com/AWSSimpleQueueService/latest/SQSDeveloperGuide/sqs-message-timers.html
    /// <summary>
    /// A message under per-message DelaySeconds is withheld (>=3x an undelayed control's latency)
    /// and counted as Delayed (polled to >=1, then Visible 0, NotVisible 0, Delayed 1); a short delay
    /// is delivered once it expires, and PurgeQueue empties all three counters.
    ///
    /// Reference: https://docs.aws.amazon.com/AWSSimpleQueueService/latest/APIReference/API_GetQueueAttributes.html
    /// Reference: https://docs.aws.amazon.com/AWSSimpleQueueService/latest/SQSDeveloperGuide/sqs-message-timers.html
    /// </summary>
    [Test]
    [DisplayName("sqs.delayed-messages-are-counted-separately-from-visible-and-in-flight")]
    [Timeout(FidelityContext.RealTimeBudgetMilliseconds)]
    [Category(ApplyDefaultCategoryAttribute.TimeBasedCategoryName)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.RealTimeBudgetMilliseconds, cancellationToken);
        var sqs = ctx.Sqs;
        // DelaySeconds is left at the queue default of 0, so every delay observed below comes from the
        // per-message parameter and cannot be a queue-level effect.
        var url = (await sqs.CreateQueueAsync(new CreateQueueRequest { QueueName = FidelityContext.UniqueName("sim2real-behav-"), Tags = new(Tags) })).QueueUrl;
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(url));

        var queueDelay = (await sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest { QueueUrl = url, AttributeNames = ["DelaySeconds"] }))
            .Attributes!["DelaySeconds"];
        Ensure(queueDelay == "0",
            $"the queue's own DelaySeconds is {queueDelay}, not '0'; every assertion below would be " +
            "unable to attribute a delay to the per-message parameter");

        var token = Guid.NewGuid().ToString("N")[..8];
        var controlBody = $"undelayed-control-{token}";
        var shortBody = $"short-delay-{token}";
        var longBody = $"long-delay-{token}";

        // --- positive control: an undelayed message, and its measured latency L --------
        await sqs.SendMessageAsync(new SendMessageRequest { QueueUrl = url, MessageBody = controlBody });
        var latencySeconds = await AwaitBody(sqs, url, controlBody, timeoutSeconds: 40.0, what: "the undelayed positive control");
        var absenceWindowSeconds = Math.Max(AbsenceMultiplier * latencySeconds, 3.0);
        Ensure(absenceWindowSeconds < ShortDelaySeconds,
            $"the undelayed control took {latencySeconds:F2}s, so a {AbsenceMultiplier}x absence window " +
            $"is {absenceWindowSeconds:F1}s -- that is not shorter than the {ShortDelaySeconds}s delay under " +
            "test, so the withheld-message leg cannot distinguish 'delayed' from 'slow'");

        // --- the subjects: one short delay (waited out), one long delay (counted) ----------
        var sendStart = Stopwatch.StartNew();
        await sqs.SendMessageAsync(new SendMessageRequest { QueueUrl = url, MessageBody = shortBody, DelaySeconds = ShortDelaySeconds });
        await sqs.SendMessageAsync(new SendMessageRequest { QueueUrl = url, MessageBody = longBody, DelaySeconds = LongDelaySeconds });

        // Withheld: for >=3L nothing is delivered, even though an identical undelayed send on this very
        // queue was delivered in L seconds moments ago.
        var absence = Stopwatch.StartNew();
        while (absence.Elapsed.TotalSeconds < absenceWindowSeconds)
        {
            var leaked = await Receive(sqs, url);
            Ensure(leaked.Count == 0,
                $"a delayed message was delivered {sendStart.Elapsed.TotalSeconds:F2}s after the send, " +
                $"inside its DelaySeconds window: [{string.Join(", ", leaked.Select(m => m.Body))}]. The undelayed " +
                $"control on this same queue took {latencySeconds:F2}s, so this is not slowness");
        }

        // Second independent observable for the same absence: they are DELAYED, not merely invisible.
        // The counter may lag the sends (SQS GetQueueAttributes docs), so it is polled; the long
        // message stays delayed for _LONG_DELAY_S, far past this poll's budget.
        const double delayedWaitSeconds = 60.0;
        var delayedBudgetSeconds = BudgetSeconds(ctx, delayedWaitSeconds);
        var delayedStart = Stopwatch.StartNew();
        var delayedStop = Math.Min(delayedWaitSeconds, delayedBudgetSeconds);
        var delayedNow = (await GetCounters(sqs, url))["ApproximateNumberOfMessagesDelayed"];
        while (int.Parse(delayedNow) < 1 && delayedStart.Elapsed.TotalSeconds < delayedStop)
        {
            await Task.Delay(TimeSpan.FromSeconds(1));
            delayedNow = (await GetCounters(sqs, url))["ApproximateNumberOfMessagesDelayed"];
        }

        if (int.Parse(delayedNow) < 1 && delayedBudgetSeconds < delayedWaitSeconds)
        {
            throw new TimeoutException(
                $"the check's time budget ran out after {delayedStart.Elapsed.TotalSeconds:F1}s, before " +
                $"the {delayedWaitSeconds}s ApproximateNumberOfMessagesDelayed wait ended; not graded");
        }

        Ensure(int.Parse(delayedNow) >= 1,
            "two messages are inside their DelaySeconds windows but " +
            $"ApproximateNumberOfMessagesDelayed is {delayedNow}; a target that hides delayed " +
            "messages without accounting for them reports 0 here");

        // --- delivery is DEMANDED once the short delay expires ----------------------------
        var remainingSeconds = ShortDelaySeconds - sendStart.Elapsed.TotalSeconds;
        var graceSeconds = Math.Max(remainingSeconds, 0.0) + absenceWindowSeconds + 20.0;
        var shortLatencySeconds = await AwaitBody(
            sqs,
            url,
            shortBody,
            timeoutSeconds: graceSeconds,
            what: $"a message sent with DelaySeconds={ShortDelaySeconds} must become receivable once the " +
                  "delay expires");
        var totalSeconds = sendStart.Elapsed.TotalSeconds;
        Ensure(totalSeconds >= ShortDelaySeconds,
            $"the short-delay message was delivered {totalSeconds:F2}s after its send, before its " +
            $"DelaySeconds={ShortDelaySeconds} window closed (the receive itself took " +
            $"{shortLatencySeconds:F2}s)");

        // --- the three-way accounting, on one instant's read ------------------------------
        var settled = await AwaitCounters(
            sqs,
            url,
            new()
            {
                ["ApproximateNumberOfMessages"] = "0",
                ["ApproximateNumberOfMessagesNotVisible"] = "0",
                ["ApproximateNumberOfMessagesDelayed"] = "1",
            },
            timeoutSeconds: CounterSeconds,
            why: $"exactly one message ({longBody}, DelaySeconds={LongDelaySeconds}) remains and it is " +
                 "delayed, so it must be counted as Delayed and in neither of the other two buckets. " +
                 "The documented counter-consistency window is >=1 minute");
        Ensure(settled["ApproximateNumberOfMessagesDelayed"] == "1",
            $"expected exactly one delayed message, got {Show(settled)}");

        // --- terminal read at the destination: the delayed message is still unreachable ---
        var stillHidden = await Receive(sqs, url, waitSeconds: 2);
        Ensure(stillHidden.Count == 0,
            $"the message under DelaySeconds={LongDelaySeconds} was delivered " +
            $"{sendStart.Elapsed.TotalSeconds:F1}s after its send: [{string.Join(", ", stillHidden.Select(m => m.Body))}]");

        // --- PurgeQueue must clear the Delayed bucket too ---------------------------------
        await sqs.PurgeQueueAsync(new PurgeQueueRequest { QueueUrl = url });
        await AwaitCounters(
            sqs,
            url,
            Counters.ToDictionary(name => name, _ => "0"),
            timeoutSeconds: PurgeSeconds,
            why: "PurgeQueue deletes all messages, so a delayed message must leave the Delayed bucket " +
                 "as well -- a purge that only clears the Visible bucket strands the delayed message");
    }

    /// <summary>All three approximate counters in ONE GetQueueAttributes, so they describe one instant.</summary>
    private static async Task<Dictionary<string, string>> GetCounters(AmazonSQSClient sqs, string url)
    {
        var attrs = (await sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest { QueueUrl = url, AttributeNames = [.. Counters] })).Attributes!;
        var missing = Counters.Where(name => !attrs.ContainsKey(name)).ToList();
        Ensure(missing.Count == 0,
            $"GetQueueAttributes did not return [{string.Join(", ", missing)}]; SQS documents all three as supported " +
            "attribute names, and a target that omits Delayed cannot be graded on delay accounting. " +
            $"Got keys [{string.Join(", ", attrs.Keys.Order(StringComparer.Ordinal))}]");
        return Counters.ToDictionary(name => name, name => attrs[name]);
    }

    /// <summary>A poll's timeout: cap_s, bounded by the runner's remaining time less 5 s.</summary>
    private static double BudgetSeconds(FidelityContext ctx, double capSeconds)
    {
        var left = ctx.TimeLeftSeconds;
        return Math.Max(1.0, Math.Min(capSeconds, left - 5.0));
    }

    /// <summary>Bounded poll until every named counter equals its wanted value; RAISES on the deadline.</summary>
    private static async Task<Dictionary<string, string>> AwaitCounters(
        AmazonSQSClient sqs, string url, Dictionary<string, string> want, double timeoutSeconds, string why)
    {
        var deadline = Stopwatch.StartNew();
        Dictionary<string, string>? last = null;
        while (deadline.Elapsed.TotalSeconds < timeoutSeconds)
        {
            last = await GetCounters(sqs, url);
            if (want.All(kv => last[kv.Key] == kv.Value))
            {
                return last;
            }
        }

        throw Fail(
            $"the queue counters never reached {Show(want)} within {timeoutSeconds}s (last={(last is null ? "None" : Show(last))}); {why}");
    }

    private static async Task<List<Message>> Receive(AmazonSQSClient sqs, string url, int waitSeconds = 1)
        => (await sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = url, MaxNumberOfMessages = 10, WaitTimeSeconds = waitSeconds })).Messages ?? [];

    /// <summary>Bounded poll until `body` is delivered; return the measured latency, raise on timeout.</summary>
    private static async Task<double> AwaitBody(AmazonSQSClient sqs, string url, string body, double timeoutSeconds, string what)
    {
        var start = Stopwatch.StartNew();
        while (start.Elapsed.TotalSeconds < timeoutSeconds)
        {
            foreach (var msg in await Receive(sqs, url))
            {
                if (msg.Body == body)
                {
                    await sqs.DeleteMessageAsync(new DeleteMessageRequest { QueueUrl = url, ReceiptHandle = msg.ReceiptHandle });
                    return start.Elapsed.TotalSeconds;
                }

                throw Fail(
                    $"{what}: an unexpected message {msg.Body} was delivered while waiting for " +
                    $"{body}; this queue should carry nothing else");
            }
        }

        throw Fail($"{what}: {body} was not delivered within {timeoutSeconds}s");
    }

    private static string Show(Dictionary<string, string> d) => "{" + string.Join(", ", d.Select(kv => $"'{kv.Key}': '{kv.Value}'")) + "}";
}
