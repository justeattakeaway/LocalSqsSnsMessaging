// Translated from behavioral/sqs/check_queue_moves_an_exact_message_and_the_deleted_queue_is_gone.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Amazon.SQS;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sqs;

/// <summary>sqs.queue-moves-an-exact-message-and-the-deleted-queue-is-gone</summary>
[Category("AwsEvaluationSuite")]
public sealed class QueueMovesAnExactMessageAndTheDeletedQueueIsGone
{
    private const string QueueNotFound = "AWS.SimpleQueueService.NonExistentQueue";

    // DeleteQueue is documented to take up to 60 s to propagate; the counters converge asynchronously.
    private const double GoneWaitSeconds = 240.0;
    private const double DrainWaitSeconds = 120.0;
    private const double ReceiveWaitSeconds = 60.0;

    private static async Task<T> PollUntil<T>(Func<Task<T>> probe, Func<T, bool> predicate, double timeoutSeconds, double intervalSeconds, string description)
    {
        var deadline = Stopwatch.StartNew();
        while (true)
        {
            var last = await probe();
            if (predicate(last))
            {
                return last;
            }

            if (deadline.Elapsed >= TimeSpan.FromSeconds(timeoutSeconds))
            {
                throw Fail($"{description} not observed within {timeoutSeconds:F0}s; last saw {last}");
            }

            await Task.Delay(TimeSpan.FromSeconds(intervalSeconds));
        }
    }

    /// <summary>Poll fn until it fails with code; return that error response. FAILS on the deadline.</summary>
    private static async Task<AwsError> PollUntilCode(Func<Task> fn, string code, double timeoutSeconds, double intervalSeconds, string what)
    {
        var deadline = Stopwatch.StartNew();
        var seen = "<call succeeded>";
        while (true)
        {
            var error = await FidelityContext.CaptureError(fn);
            if (error is null)
            {
                seen = "<call succeeded>";
            }
            else
            {
                seen = error.Code ?? "None";
                if (seen == code)
                {
                    return error;
                }
            }

            if (deadline.Elapsed >= TimeSpan.FromSeconds(timeoutSeconds))
            {
                throw Fail($"{what}: expected {code}, got '{seen}' after {timeoutSeconds:F0}s");
            }

            await Task.Delay(TimeSpan.FromSeconds(intervalSeconds));
        }
    }

    private static void AssertRefusal(AwsError response, string what)
    {
        var status = (int)response.StatusCode;
        Ensure(status is >= 400 and < 500, $"{what} answered HTTP {status}, expected 4xx");
        Ensure(!string.IsNullOrEmpty(response.Message), $"{what} answered an empty error message");
    }

    private static async Task<List<string>> ListUrls(AmazonSQSClient sqs, string prefix)
    {
        var urls = new List<string>();
        string? token = null;
        while (true)
        {
            var request = new ListQueuesRequest { QueueNamePrefix = prefix };
            if (token is not null)
            {
                request.NextToken = token;
            }

            var page = await sqs.ListQueuesAsync(request);
            urls.AddRange(page.QueueUrls ?? []);
            token = page.NextToken;
            if (string.IsNullOrEmpty(token))
            {
                return urls;
            }
        }
    }

    private static async Task DeleteQueueIfPresent(AmazonSQSClient sqs, string url)
    {
        var error = await FidelityContext.CaptureError(() => sqs.DeleteQueueAsync(new DeleteQueueRequest { QueueUrl = url }));
        if (error is not null && error.Code != QueueNotFound)
        {
            throw error.Exception;
        }
    }

    /// <summary>
    /// A queue moves one exact message (sent, received byte-exact with its MD5 and MessageId,
    /// deleted, drained), then the deleted queue is gone: read by URL answers NonExistentQueue,
    /// the listing filtered to its name drops it, and SendMessage on the dead URL is refused.
    /// </summary>
    [Test]
    [DisplayName("sqs.queue-moves-an-exact-message-and-the-deleted-queue-is-gone")]
    [Timeout(FidelityContext.BudgetMilliseconds)]
    [KnownFidelityGap(FidelityGap.QueryErrorCodes, "Check timed out after 60.0s (the deleted queue answers QueueDoesNotExist, never AWS.SimpleQueueService.NonExistentQueue)")]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sqs = ctx.Sqs;
        var name = FidelityContext.UniqueName("sim2real-behav-");
        var url = (await sqs.CreateQueueAsync(new CreateQueueRequest { QueueName = name })).QueueUrl;
        ctx.OnCleanup(() => DeleteQueueIfPresent(sqs, url));

        const string body = "move-me-exactly";
        var sent = await sqs.SendMessageAsync(new SendMessageRequest { QueueUrl = url, MessageBody = body });
        var received = await PollUntil(
            async () => (await sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = url, MaxNumberOfMessages = 1, WaitTimeSeconds = 5 })).Messages ?? [],
            msgs => msgs.Count == 1,
            timeoutSeconds: ReceiveWaitSeconds,
            intervalSeconds: 1,
            description: "the sent message being received");
        var msg = received[0];
        Ensure(msg.Body == body, $"received body '{msg.Body}', sent '{body}'");
        Ensure(msg.MD5OfBody == Convert.ToHexStringLower(MD5.HashData(Encoding.UTF8.GetBytes(body))),
            $"MD5OfBody '{msg.MD5OfBody}' is not the MD5 of the sent body");
        Ensure(msg.MessageId == sent.MessageId, $"received MessageId '{msg.MessageId}', SendMessage returned '{sent.MessageId}'");
        await sqs.DeleteMessageAsync(new DeleteMessageRequest { QueueUrl = url, ReceiptHandle = msg.ReceiptHandle });
        // A message that was not deleted stays counted, in flight or visible again, so the poll fails.
        await PollUntil(
            async () => (await sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest
            {
                QueueUrl = url,
                AttributeNames = ["ApproximateNumberOfMessages", "ApproximateNumberOfMessagesNotVisible"],
            })).Attributes ?? throw new KeyNotFoundException("Attributes"),
            a => a.GetValueOrDefault("ApproximateNumberOfMessages") == "0" && a.GetValueOrDefault("ApproximateNumberOfMessagesNotVisible") == "0",
            timeoutSeconds: DrainWaitSeconds,
            intervalSeconds: 2,
            description: "the queue counters draining to 0");

        await sqs.DeleteQueueAsync(new DeleteQueueRequest { QueueUrl = url });
        var read = await PollUntilCode(
            () => sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest { QueueUrl = url, AttributeNames = ["QueueArn"] }),
            QueueNotFound,
            timeoutSeconds: GoneWaitSeconds,
            intervalSeconds: 10,
            what: "GetQueueAttributes on the deleted queue");
        AssertRefusal(read, "GetQueueAttributes on the deleted queue");
        await PollUntil(
            () => ListUrls(sqs, name),
            urls => !urls.Contains(url),
            timeoutSeconds: GoneWaitSeconds,
            intervalSeconds: 10,
            description: "the deleted queue leaving ListQueues");
        var deadSend = await PollUntilCode(
            () => sqs.SendMessageAsync(new SendMessageRequest { QueueUrl = url, MessageBody = "after-delete" }),
            QueueNotFound,
            timeoutSeconds: GoneWaitSeconds,
            intervalSeconds: 10,
            what: "SendMessage on the deleted queue");
        AssertRefusal(deadSend, "SendMessage on the deleted queue");
    }
}
