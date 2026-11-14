// Translated from behavioral/sqs/check_server_side_message_retention_auto_expunge.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Diagnostics;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sqs;

/// <summary>sqs.server-side-message-retention-auto-expunge</summary>
[Category("AwsEvaluationSuite")]
public sealed class ServerSideMessageRetentionAutoExpunge
{
    private const string Check = "server-side-message-retention-auto-expunge";

    // MessageRetentionPeriod's documented minimum is 60 seconds (max 14 days):
    // https://docs.aws.amazon.com/AWSSimpleQueueService/latest/SQSDeveloperGuide/sqs-basic-architecture.html
    private const int RetentionSeconds = 60;

    /// <summary>
    /// With the minimum MessageRetentionPeriod (60s), an unconsumed, never-deleted message is
    /// expunged once the period elapses: the approximate count reaches 0 and ReceiveMessage
    /// returns nothing.
    /// </summary>
    // timing:real-time — retention: 60 s https://docs.aws.amazon.com/AWSSimpleQueueService/latest/APIReference/API_SetQueueAttributes.html
    [Test]
    [DisplayName("sqs.server-side-message-retention-auto-expunge")]
    [Timeout(FidelityContext.RealTimeBudgetMilliseconds)]
    [Category(ApplyDefaultCategoryAttribute.TimeBasedCategoryName)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.RealTimeBudgetMilliseconds, cancellationToken);
        var sqs = ctx.Sqs;
        const string body = "expire-me-without-being-deleted";
        var url = (await sqs.CreateQueueAsync(new CreateQueueRequest
        {
            QueueName = FidelityContext.UniqueName("sim2real-behav-retain-"),
            Attributes = new() { ["MessageRetentionPeriod"] = RetentionSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture) },
            Tags = new() { ["sim2real:managed"] = "true", ["sim2real"] = Check },
        })).QueueUrl;
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(url));

        await sqs.SendMessageAsync(new SendMessageRequest { QueueUrl = url, MessageBody = body });

        // Sanity: the message is actually there right after sending (never received/deleted).
        var count = (await sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest
        {
            QueueUrl = url,
            AttributeNames = ["ApproximateNumberOfMessages"],
        })).Attributes!["ApproximateNumberOfMessages"];
        Ensure(count == "1", $"expected the sent message to be present, saw count='{count}'");

        // Prove it is genuinely RECEIVABLE, not merely counted (VisibilityTimeout=0 so this
        // confirmation cannot hide the message behind its own visibility window).
        var preMsgs = (await sqs.ReceiveMessageAsync(new ReceiveMessageRequest
        {
            QueueUrl = url,
            MaxNumberOfMessages = 1,
            WaitTimeSeconds = 1,
            VisibilityTimeout = 0,
        })).Messages ?? [];
        Ensure(preMsgs.Select(m => m.Body).SequenceEqual([body]),
            "the message was counted but never receivable before expiry, so the disappearance " +
            $"below would not prove retention: got [{string.Join(", ", preMsgs.Select(m => m.Body))}]");

        // Poll (bounded, well past the 60s retention window) for the server-side expunge.
        await PollUntilExpunged(ctx, url, timeoutSeconds: 180.0);
    }

    private static async Task PollUntilExpunged(FidelityContext ctx, string url, double timeoutSeconds = 180.0, double intervalSeconds = 5.0)
    {
        var sqs = ctx.Sqs;
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(timeoutSeconds))
        {
            var count = (await sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest
            {
                QueueUrl = url,
                AttributeNames = ["ApproximateNumberOfMessages"],
            })).Attributes!["ApproximateNumberOfMessages"];
            if (count == "0")
            {
                // VisibilityTimeout=0: the confirmation read must never hide the message
                // behind its own visibility window and get misread as a permanent expunge
                // (the default queue VisibilityTimeout is 30s, well inside this poll's window).
                var resp = await sqs.ReceiveMessageAsync(new ReceiveMessageRequest
                {
                    QueueUrl = url,
                    MaxNumberOfMessages = 10,
                    WaitTimeSeconds = 1,
                    VisibilityTimeout = 0,
                });
                if (resp.Messages is null or { Count: 0 })
                {
                    return;
                }
            }

            await Task.Delay(TimeSpan.FromSeconds(intervalSeconds));
        }

        throw Fail($"message was not auto-expunged within {timeoutSeconds}s of a {RetentionSeconds}s MessageRetentionPeriod");
    }
}
