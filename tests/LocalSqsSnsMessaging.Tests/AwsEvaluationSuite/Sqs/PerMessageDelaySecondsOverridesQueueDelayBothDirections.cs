// Translated from behavioral/sqs/check_per_message_delay_seconds_overrides_queue_delay_both_directions.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Globalization;
using Amazon.SQS;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sqs;

/// <summary>sqs.per-message-delay-seconds-overrides-queue-delay-both-directions</summary>
[Category("AwsEvaluationSuite")]
public sealed class PerMessageDelaySecondsOverridesQueueDelayBothDirections
{
    private const int DelaySeconds = 15;

    // A 15s-delayed message cannot be receivable within a few seconds, so its absence is an invariant;
    // the immediate drain retries for presence but stays well inside _DELAY_S.
    private const int ImmediatePolls = 2;
    private const int ImmediateWaitSeconds = 2;

    private static Dictionary<string, string> Tags => new() { ["sim2real:managed"] = "true", ["sim2real"] = "delay-seconds-override" };

    /// <summary>Receive-and-delete into the set `into` until `empty_polls` consecutive empties or deadline.</summary>
    private static async Task<HashSet<string>> Drain(AmazonSQSClient sqs, string url, HashSet<string> into, double timeoutSeconds, int waitSeconds, int emptyPolls)
    {
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
                into.Add(m.Body);
                await sqs.DeleteMessageAsync(new DeleteMessageRequest { QueueUrl = url, ReceiptHandle = m.ReceiptHandle });
            }
        }

        return into;
    }

    private static string Sorted(IEnumerable<string> values) => "[" + string.Join(", ", values.Order(StringComparer.Ordinal).Select(v => $"'{v}'")) + "]";

    /// <summary>
    /// A per-message DelaySeconds overrides the queue's in both directions: DelaySeconds=0 on a
    /// 15s-delay queue is receivable at once, and DelaySeconds=15 on a 0s queue is not.
    /// </summary>
    [Test]
    [DisplayName("sqs.per-message-delay-seconds-overrides-queue-delay-both-directions")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sqs = ctx.Sqs;
        var token = Guid.NewGuid().ToString("N")[..8];
        var delay = DelaySeconds.ToString(CultureInfo.InvariantCulture);

        var urlSlow = (await sqs.CreateQueueAsync(new CreateQueueRequest
        {
            QueueName = FidelityContext.UniqueName("sim2real-behav-") + "-slow",
            Attributes = new() { ["DelaySeconds"] = delay },
            Tags = Tags,
        })).QueueUrl;
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(urlSlow));

        var urlFast = (await sqs.CreateQueueAsync(new CreateQueueRequest
        {
            QueueName = FidelityContext.UniqueName("sim2real-behav-") + "-fast",
            Tags = Tags,
        })).QueueUrl;
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(urlFast));

        var slowAttrs = (await sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest { QueueUrl = urlSlow, AttributeNames = ["All"] })).Attributes!;
        Ensure(slowAttrs.GetValueOrDefault("DelaySeconds") == delay,
            $"the delayed queue did not take DelaySeconds={DelaySeconds}, so neither override direction " +
            $"below is being tested: DelaySeconds='{slowAttrs.GetValueOrDefault("DelaySeconds")}'");
        var fastAttrs = (await sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest { QueueUrl = urlFast, AttributeNames = ["All"] })).Attributes!;
        Ensure(fastAttrs.GetValueOrDefault("DelaySeconds") == "0",
            $"the undelayed queue must carry the DEFAULT DelaySeconds=0: '{fastAttrs.GetValueOrDefault("DelaySeconds")}'");

        // An explicit 0 must not be treated as "not supplied" and fall back to the queue's delay.
        var markerA = $"a-msgdelay0-on-slowq-{token}";     // explicit 0 must BEAT the queue's 15
        var markerB = $"b-inherits15-on-slowq-{token}";    // no parameter -> inherits the queue's 15
        var markerC = $"c-msgdelay15-on-fastq-{token}";    // explicit 15 must BEAT the queue's 0
        var markerD = $"d-inherits0-on-fastq-{token}";     // no parameter -> inherits the queue's 0

        await sqs.SendMessageAsync(new SendMessageRequest { QueueUrl = urlSlow, MessageBody = markerA, DelaySeconds = 0 });
        await sqs.SendMessageAsync(new SendMessageRequest { QueueUrl = urlSlow, MessageBody = markerB });
        await sqs.SendMessageAsync(new SendMessageRequest { QueueUrl = urlFast, MessageBody = markerC, DelaySeconds = DelaySeconds });
        await sqs.SendMessageAsync(new SendMessageRequest { QueueUrl = urlFast, MessageBody = markerD });

        // --- immediate phase: bounded well inside the 15s boundary -------------------------
        var immediateSlow = new HashSet<string>();
        var immediateFast = new HashSet<string>();
        await Drain(sqs, urlSlow, immediateSlow, timeoutSeconds: ImmediatePolls * ImmediateWaitSeconds + 2, waitSeconds: ImmediateWaitSeconds, emptyPolls: ImmediatePolls);
        await Drain(sqs, urlFast, immediateFast, timeoutSeconds: ImmediatePolls * ImmediateWaitSeconds + 2, waitSeconds: ImmediateWaitSeconds, emptyPolls: ImmediatePolls);

        Ensure(immediateSlow.SetEquals([markerA]),
            $"on a DelaySeconds={DelaySeconds} queue, exactly the message that overrode with " +
            $"DelaySeconds=0 may be immediately receivable: got {Sorted(immediateSlow)}, expected {Sorted([markerA])}");
        Ensure(immediateFast.SetEquals([markerD]),
            $"on a DelaySeconds=0 queue, the message that overrode with DelaySeconds={DelaySeconds} must " +
            $"NOT be immediately receivable: got {Sorted(immediateFast)}, expected {Sorted([markerD])}");

        // --- eventual phase: both delayed messages must arrive -----------------------------
        var eventualSlow = await Drain(sqs, urlSlow, [.. immediateSlow], timeoutSeconds: 90, waitSeconds: 5, emptyPolls: 3);
        var eventualFast = await Drain(sqs, urlFast, [.. immediateFast], timeoutSeconds: 90, waitSeconds: 5, emptyPolls: 3);

        Ensure(eventualSlow.SetEquals([markerA, markerB]),
            $"the queue-delayed message never arrived on the delayed queue: got " +
            $"{Sorted(eventualSlow)}, expected {Sorted([markerA, markerB])}");
        Ensure(eventualFast.SetEquals([markerC, markerD]),
            $"the message-delayed message never arrived on the undelayed queue: got " +
            $"{Sorted(eventualFast)}, expected {Sorted([markerC, markerD])}");
    }
}
