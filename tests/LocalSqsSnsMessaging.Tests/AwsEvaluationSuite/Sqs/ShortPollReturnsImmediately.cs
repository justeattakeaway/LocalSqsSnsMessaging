// Translated from behavioral/sqs/check_short_poll_returns_immediately.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sqs;

/// <summary>sqs.short-poll-returns-immediately</summary>
[Category("AwsEvaluationSuite")]
public sealed class ShortPollReturnsImmediately
{
    private static Dictionary<string, string> Tags() => new() { ["sim2real:managed"] = "true", ["sim2real"] = "short-poll-returns-immediately" };

    /// <summary>
    /// ReceiveMessage(WaitTimeSeconds=0) on an empty queue returns promptly (&lt;1s).
    ///
    /// Guards the opposite failure mode from long-poll: an emulator that always
    /// blocks (or sleeps a fixed amount) regardless of WaitTimeSeconds.
    /// </summary>
    [Test]
    [DisplayName("sqs.short-poll-returns-immediately")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sqs = ctx.Sqs;
        var url = (await sqs.CreateQueueAsync(new CreateQueueRequest { QueueName = FidelityContext.UniqueName("sim2real-behav-"), Tags = Tags() })).QueueUrl;
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(url));
        var start = FidelityTime.StartNew();
        var resp = await sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = url, MaxNumberOfMessages = 1, WaitTimeSeconds = 0 });
        var elapsed = start.Elapsed.TotalSeconds;
        var messages = resp.Messages ?? [];
        if (messages.Count > 0)
        {
            throw Fail($"expected empty receive on an empty queue, got {messages.Count} message(s)");
        }

        // elapsed-bound: a short poll (WaitTimeSeconds=0) does not wait; returning promptly is the claim.
        if (elapsed > 1.0)
        {
            throw Fail($"short poll (WaitTimeSeconds=0) took {elapsed:F3}s; expected <1s");
        }
    }
}
