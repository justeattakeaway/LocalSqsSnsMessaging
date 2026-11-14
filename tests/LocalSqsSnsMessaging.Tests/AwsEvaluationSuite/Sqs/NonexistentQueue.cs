// Translated from behavioral/sqs/check_nonexistent_queue.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sqs;

/// <summary>sqs.nonexistent-queue</summary>
[Category("AwsEvaluationSuite")]
public sealed class NonexistentQueue
{
    private const string Check = "nonexistent-queue";
    private const string ErrNonexistentQueue = "AWS.SimpleQueueService.NonExistentQueue";

    /// <summary>
    /// receive_message on non-existent QueueUrl → NonExistentQueue, with a positive
    /// control on a real queue so the negative can't be confused with a target where
    /// receive_message always errors.
    /// </summary>
    [Test]
    [DisplayName("sqs.nonexistent-queue")]
    [Timeout(FidelityContext.BudgetMilliseconds)]
    [KnownFidelityGap(FidelityGap.QueryErrorCodes, "expected AWS.SimpleQueueService.NonExistentQueue, got QueueDoesNotExist")]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sqs = ctx.Sqs;
        var url = (await sqs.CreateQueueAsync(new CreateQueueRequest
        {
            QueueName = FidelityContext.UniqueName("sim2real-behav-"),
            Tags = new() { ["sim2real-check"] = Check },
        })).QueueUrl;
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(url));

        var control = await sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = url, MaxNumberOfMessages = 1 });
        Ensure(control.Messages is null, $"expected no messages on fresh queue, got {control.Messages?.Count}");

        var ghost = $"{url[..url.LastIndexOf('/')]}/{Guid.NewGuid().ToString("N")[..8]}-ghost";
        var code = await FidelityContext.CaptureError(() => sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = ghost, MaxNumberOfMessages = 1 }));
        Ensure(code is { Code: ErrNonexistentQueue, Message: "The specified queue does not exist." },
            $"expected {ErrNonexistentQueue}, got {code}");
    }
}
