// Translated from behavioral/sqs/check_purge_queue_in_progress_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Diagnostics;
using System.Globalization;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sqs;

/// <summary>sqs.purge-queue-in-progress-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class PurgeQueueInProgressRejected
{
    private const string PurgeInProgress = "AWS.SimpleQueueService.PurgeQueueInProgress";

    /// <summary>
    /// A second PurgeQueue call within 60 seconds of a still-pending purge is rejected with
    /// PurgeQueueInProgress, while the first purge itself succeeds.
    /// </summary>
    [Test]
    [DisplayName("sqs.purge-queue-in-progress-rejected")]
    [Timeout(FidelityContext.BudgetMilliseconds)]
    [KnownFidelityGap(FidelityGap.Validation, "a second PurgeQueue within 60 seconds succeeded instead of being rejected with PurgeQueueInProgress")]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sqs = ctx.Sqs;
        var queueName = FidelityContext.UniqueName("sim2real-sqs-purgeinprog-");
        var url = (await sqs.CreateQueueAsync(new CreateQueueRequest { QueueName = queueName })).QueueUrl;
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(url));

        await sqs.SendMessageAsync(new SendMessageRequest { QueueUrl = url, MessageBody = "to-be-purged" });
        await PollUntil(
            async () => int.Parse(
                (await sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest { QueueUrl = url, AttributeNames = ["ApproximateNumberOfMessages"] }))
                    .Attributes!["ApproximateNumberOfMessages"],
                CultureInfo.InvariantCulture),
            n => n >= 1,
            timeoutSeconds: 15.0);

        await sqs.PurgeQueueAsync(new PurgeQueueRequest { QueueUrl = url }); // first purge: positive control -- succeeds

        // Real AWS returns the wire error code prefixed with the service namespace:
        // "AWS.SimpleQueueService.PurgeQueueInProgress". A message sent during the first
        // purge's window may or may not be deleted by that purge, so no message is graded here.
        var (code, message) = await ExpectRejected(() => sqs.PurgeQueueAsync(new PurgeQueueRequest { QueueUrl = url }));
        Ensure(code == PurgeInProgress, $"expected {PurgeInProgress}, got '{code}'");
        Ensure(message == $"Only one PurgeQueue operation on {queueName} is allowed every 60 seconds.",
            $"unexpected message: '{message}'");
    }

    /// <summary>Call fn(); return the ClientError (Code, Message). An unexpected SUCCESS raises.</summary>
    private static async Task<(string? Code, string? Message)> ExpectRejected(Func<Task<PurgeQueueResponse>> fn)
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is not null)
        {
            return (error.Code, error.Message);
        }

        throw Fail($"expected the call to be rejected, but it SUCCEEDED and returned {{HTTPStatusCode: {result!.HttpStatusCode}}}");
    }

    private static async Task<T> PollUntil<T>(Func<Task<T>> probe, Func<T, bool> predicate, double timeoutSeconds = 30.0, double intervalSeconds = 1.0)
    {
        var deadline = Stopwatch.StartNew();
        T? last = default;
        while (deadline.Elapsed < TimeSpan.FromSeconds(timeoutSeconds))
        {
            last = await probe();
            if (predicate(last))
            {
                return last;
            }

            await Task.Delay(TimeSpan.FromSeconds(intervalSeconds));
        }

        throw Fail($"condition not met within {timeoutSeconds}s (last={last})");
    }
}
