// Translated from behavioral/sqs/check_cli_sqs_nonexistent_queue_error.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sqs;

/// <summary>sqs.cli-sqs-nonexistent-queue-error</summary>
[Category("AwsEvaluationSuite")]
public sealed class CliSqsNonexistentQueueError
{
    /// <summary>
    /// Sending to a non-existent queue via CLI fails with NonExistentQueue, while the
    /// identical CLI send to a queue that exists succeeds.
    /// </summary>
    [Test]
    [DisplayName("sqs.cli-sqs-nonexistent-queue-error")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        // The caller's own account: a foreign account in the queue URL is refused as
        // InvalidAddress before the queue-existence check is reached.
        var acct = ctx.Account;
        var sqs = ctx.Sqs;
        var url = (await sqs.CreateQueueAsync(new CreateQueueRequest
        {
            QueueName = FidelityContext.UniqueName("sim2real-behav-"),
            Tags = new() { ["sim2real:managed"] = "true" },
        })).QueueUrl;
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(url));

        // Positive control: the same CLI send against a queue that exists succeeds.
        // (`aws sqs send-message --queue-url URL --message-body x` is this SendMessage call.)
        var ok = await FidelityContext.CaptureError(() => sqs.SendMessageAsync(new SendMessageRequest { QueueUrl = url, MessageBody = "x" }));
        Ensure(ok is null, $"expected the CLI send to an existing queue to succeed, got {ok}");

        var proc = await FidelityContext.CaptureError(() => sqs.SendMessageAsync(new SendMessageRequest
        {
            QueueUrl = $"{sqs.Config.ServiceURL.TrimEnd('/')}/{acct}/does-not-exist-{FidelityContext.UniqueName("")}",
            MessageBody = "x",
        }));
        Ensure(proc is not null, "Expected failure for non-existent queue");
        // The CLI prints "An error occurred (CODE) when calling ..." to stderr; the code is AwsError.Code.
        Ensure(proc.Code == "AWS.SimpleQueueService.NonExistentQueue",
            $"Expected AWS.SimpleQueueService.NonExistentQueue in stderr, got: {proc}");
    }
}
