// Translated from behavioral/sqs/check_receive_request_attempt_id_replay_resets_visibility_timeout.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Diagnostics;
using Amazon.SQS;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sqs;

/// <summary>sqs.receive-request-attempt-id-replay-resets-visibility-timeout</summary>
[Category("AwsEvaluationSuite")]
public sealed class ReceiveRequestAttemptIdReplayResetsVisibilityTimeout
{
    private const int VtS = 20;
    // The replay happens this long after the first receive, well inside the visibility window.
    private const int ReplayAtS = 12;
    // The replayed message is asserted still hidden this long after its ORIGINAL deadline.
    private const int PastOriginalS = 5;

    /// <summary>
    /// Retrying ReceiveMessage with the same ReceiveRequestAttemptId returns the same message and
    /// restarts its visibility timeout: it stays hidden past its first deadline, while an unretried
    /// twin on an identical queue reappears first.
    /// </summary>
    [Test]
    [DisplayName("sqs.receive-request-attempt-id-replay-resets-visibility-timeout")]
    [Timeout(FidelityContext.BudgetMilliseconds)]
    [KnownFidelityGap(FidelityGap.Fifo, "a retry with the same ReceiveRequestAttemptId inside the visibility window must return the same message: got []")]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sqs = ctx.Sqs;
        var clock = Stopwatch.StartNew(); // time.monotonic()
        double Now() => clock.Elapsed.TotalSeconds;
        var token = Guid.NewGuid().ToString("N")[..8];
        Dictionary<string, string> Attrs() => new() { ["FifoQueue"] = "true", ["VisibilityTimeout"] = VtS.ToString() };
        Dictionary<string, string> Tags() => new() { ["sim2real:managed"] = "true", ["sim2real"] = "rraid-replay-resets-vt" };
        var replayedUrl = (await sqs.CreateQueueAsync(new CreateQueueRequest
        {
            QueueName = FidelityContext.UniqueName("sim2real-behav-") + "-replay.fifo",
            Attributes = Attrs(),
            Tags = Tags(),
        })).QueueUrl;
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(replayedUrl));
        var twinUrl = (await sqs.CreateQueueAsync(new CreateQueueRequest
        {
            QueueName = FidelityContext.UniqueName("sim2real-behav-") + "-twin.fifo",
            Attributes = Attrs(),
            Tags = Tags(),
        })).QueueUrl;
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(twinUrl));

        await sqs.SendMessageAsync(new SendMessageRequest
        {
            QueueUrl = replayedUrl,
            MessageBody = $"replayed-{token}",
            MessageGroupId = "g",
            MessageDeduplicationId = token,
        });
        await sqs.SendMessageAsync(new SendMessageRequest
        {
            QueueUrl = twinUrl,
            MessageBody = $"twin-{token}",
            MessageGroupId = "g",
            MessageDeduplicationId = token,
        });

        var attempt = $"attempt-{token}";
        List<Message> first = [];
        var deadline = Now() + 30;
        while (first.Count == 0 && Now() < deadline)
        {
            first = await ReceiveOne(sqs, replayedUrl, waitTimeSeconds: 2, receiveRequestAttemptId: attempt);
        }

        var receivedAt = Now();
        var twin = await ReceiveOne(sqs, twinUrl, waitTimeSeconds: 5);
        Ensure(first.Count > 0 && first[0].Body == $"replayed-{token}", $"first receive gave {Show(first)}");
        Ensure(twin.Count > 0 && twin[0].Body == $"twin-{token}", $"twin receive gave {Show(twin)}");

        while (Now() < receivedAt + ReplayAtS)
        {
            await Task.Delay(TimeSpan.FromSeconds(0.5));
        }

        var replay = await ReceiveOne(sqs, replayedUrl, receiveRequestAttemptId: attempt);
        Ensure(replay.Select(m => m.MessageId).SequenceEqual([first[0].MessageId]),
            "a retry with the same ReceiveRequestAttemptId inside the visibility window must return "
            + $"the same message: got [{string.Join(", ", replay.Select(m => m.MessageId))}]");

        // --- the twin reappears on its original schedule ---------------------------------
        List<Message> twinBack = [];
        deadline = receivedAt + VtS + 30;
        while (twinBack.Count == 0 && Now() < deadline)
        {
            twinBack = await ReceiveOne(sqs, twinUrl, waitTimeSeconds: 2, receiveRequestAttemptId: Guid.NewGuid().ToString("N"));
        }

        var twinBackAt = Now();
        Ensure(twinBack.Count > 0, $"the never-retried twin did not reappear after its {VtS}s timeout");

        // --- the replayed one is still hidden past that original deadline --------------------
        var hiddenUntil = receivedAt + VtS + PastOriginalS;
        Ensure(twinBackAt < hiddenUntil,
            $"the twin took {twinBackAt - receivedAt:F0}s to reappear, past the instant the "
            + "replayed message is checked, so the comparison below would not be tested");
        while (Now() < hiddenUntil)
        {
            var early = await ReceiveOne(sqs, replayedUrl, waitTimeSeconds: 1, receiveRequestAttemptId: Guid.NewGuid().ToString("N"));
            Ensure(early.Count == 0,
                $"the replayed message came back {Now() - receivedAt:F0}s after its "
                + $"first receive, on the original {VtS}s schedule; a same-attempt retry at "
                + $"{ReplayAtS}s must restart the visibility timeout");
        }

        // --- and it reappears once the restarted timeout lapses ---------------------------
        List<Message> back = [];
        deadline = receivedAt + ReplayAtS + VtS + 30;
        while (back.Count == 0 && Now() < deadline)
        {
            back = await ReceiveOne(sqs, replayedUrl, waitTimeSeconds: 2, receiveRequestAttemptId: Guid.NewGuid().ToString("N"));
        }

        Ensure(back.Select(m => m.MessageId).SequenceEqual([first[0].MessageId]),
            $"the replayed message never reappeared after its restarted timeout: {Show(back)}");
    }

    private static async Task<List<Message>> ReceiveOne(IAmazonSQS sqs, string url, int? waitTimeSeconds = null, string? receiveRequestAttemptId = null)
    {
        var request = new ReceiveMessageRequest { QueueUrl = url, MaxNumberOfMessages = 1 };
        if (waitTimeSeconds is not null)
        {
            request.WaitTimeSeconds = waitTimeSeconds;
        }

        if (receiveRequestAttemptId is not null)
        {
            request.ReceiveRequestAttemptId = receiveRequestAttemptId;
        }

        return (await sqs.ReceiveMessageAsync(request)).Messages ?? [];
    }

    private static string Show(List<Message> msgs) => $"[{string.Join(", ", msgs.Select(m => $"{{MessageId={m.MessageId}, Body={m.Body}}}"))}]";
}
