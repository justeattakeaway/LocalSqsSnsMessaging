// Translated from behavioral/sqs/check_queue_visibility_timeout_change_spares_in_flight_messages.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Diagnostics;
using Amazon.SQS;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sqs;

/// <summary>sqs.queue-visibility-timeout-change-spares-in-flight-messages</summary>
[Category("AwsEvaluationSuite")]
public sealed class QueueVisibilityTimeoutChangeSparesInFlightMessages
{
    private const int OriginalVtS = 35;
    private const int NewVtS = 2;
    // Margin kept between the end of the "still hidden" window and the original deadline.
    private const double MarginS = 10.0;
    // Longest probe round: a 2 s receive and a long poll of _NEW_VT_S + 4 s.
    private const int ProbeRoundS = 2 + NewVtS + 4;
    // Bound on how long the lowered queue VisibilityTimeout may take to govern new receives. The probe
    // phase, including the round that crosses it, ends inside the old message's hidden window.
    private const double ConvergeS = OriginalVtS - MarginS - ProbeRoundS - 2;

    /// <summary>
    /// Lowering VisibilityTimeout governs only later receives: a message already in flight stays
    /// hidden until its original deadline, while one received after the change reappears on the new
    /// short timeout.
    /// </summary>
    [Test]
    [DisplayName("sqs.queue-visibility-timeout-change-spares-in-flight-messages")]
    [Timeout(FidelityContext.BudgetMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sqs = ctx.Sqs;
        var clock = Stopwatch.StartNew(); // time.monotonic()
        double Now() => clock.Elapsed.TotalSeconds;
        var token = Guid.NewGuid().ToString("N")[..8];
        var url = (await sqs.CreateQueueAsync(new CreateQueueRequest
        {
            QueueName = FidelityContext.UniqueName("sim2real-behav-"),
            Attributes = new() { ["VisibilityTimeout"] = OriginalVtS.ToString() },
            Tags = new() { ["sim2real:managed"] = "true", ["sim2real"] = "queue-vt-change-in-flight" },
        })).QueueUrl;
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(url));

        var oldBody = $"in-flight-before-change-{token}";
        await sqs.SendMessageAsync(new SendMessageRequest { QueueUrl = url, MessageBody = oldBody });
        List<Message> got = [];
        var deadline = Now() + 30;
        while (got.Count == 0 && Now() < deadline)
        {
            got = await Receive(sqs, url, waitS: 2);
        }

        Ensure(got.Select(m => m.Body).SequenceEqual([oldBody]),
            $"expected to receive '{oldBody}' first, got [{string.Join(", ", got.Select(m => m.Body))}]");
        var oldReceivedAt = Now();

        await sqs.SetQueueAttributesAsync(new SetQueueAttributesRequest
        {
            QueueUrl = url,
            Attributes = new() { ["VisibilityTimeout"] = NewVtS.ToString() },
        });

        // --- a message received after the change reappears on the new timeout -------------
        var hiddenUntil = oldReceivedAt + OriginalVtS - MarginS;
        List<string> seenEarly = [];
        List<string> probes = [];
        List<string> reappeared = [];
        deadline = Now() + ConvergeS;
        while (reappeared.Count == 0 && Now() < deadline)
        {
            var probe = $"received-after-change-{probes.Count}-{token}";
            probes.Add(probe);
            await sqs.SendMessageAsync(new SendMessageRequest { QueueUrl = url, MessageBody = probe });
            var first = await Receive(sqs, url, waitS: 2);
            seenEarly.AddRange(first.Select(m => m.Body));
            // A long poll returns as soon as a probe's visibility lapses.
            var again = await Receive(sqs, url, waitS: NewVtS + 4);
            seenEarly.AddRange(again.Select(m => m.Body));
            reappeared = again.Select(m => m.Body).Where(probes.Contains).ToList();
        }

        Ensure(Now() < hiddenUntil,
            "the probe phase ran past the old message's original deadline, so the in-flight half "
            + "below would not be tested");
        Ensure(reappeared.Count > 0,
            $"no message received after VisibilityTimeout={NewVtS} reappeared within "
            + $"{ConvergeS:F0}s ({probes.Count} probes), so the queue change never took effect");
        Ensure(!seenEarly.Contains(oldBody),
            $"the message already in flight under VisibilityTimeout={OriginalVtS} reappeared "
            + $"after the queue was lowered to {NewVtS}; an in-flight message keeps the timeout it "
            + "was received under");

        // Stay inside the original window: the old message must still be hidden there.
        while (Now() < hiddenUntil)
        {
            foreach (var m in await Receive(sqs, url, waitS: Math.Min(5, Math.Max(1, (int)(hiddenUntil - Now())))))
            {
                Ensure(m.Body != oldBody,
                    $"'{oldBody}' reappeared {Now() - oldReceivedAt:F0}s after its "
                    + $"receive, before its original {OriginalVtS}s timeout");
                await sqs.DeleteMessageAsync(new DeleteMessageRequest { QueueUrl = url, ReceiptHandle = m.ReceiptHandle });
            }
        }

        // --- and it does come back once the ORIGINAL timeout lapses -----------------------
        var back = false;
        deadline = oldReceivedAt + OriginalVtS + 60;
        while (!back && Now() < deadline)
        {
            foreach (var m in await Receive(sqs, url, waitS: 5))
            {
                await sqs.DeleteMessageAsync(new DeleteMessageRequest { QueueUrl = url, ReceiptHandle = m.ReceiptHandle });
                back = back || m.Body == oldBody;
            }
        }

        Ensure(back,
            $"'{oldBody}' never reappeared after its original {OriginalVtS}s timeout; it was "
            + "never deleted, so it must become receivable again");
    }

    private static async Task<List<Message>> Receive(IAmazonSQS sqs, string url, int waitS) =>
        (await sqs.ReceiveMessageAsync(new ReceiveMessageRequest
        {
            QueueUrl = url,
            MaxNumberOfMessages = 10,
            WaitTimeSeconds = waitS,
        })).Messages ?? [];
}
