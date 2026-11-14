// Translated from behavioral/sqs/check_fifo_queue_moves_an_exact_multibyte_message_and_the_deleted_fifo_queue_is_gone.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Amazon.Runtime;
using Amazon.SQS;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sqs;

/// <summary>sqs.fifo-queue-moves-an-exact-multibyte-message-and-the-deleted-fifo-queue-is-gone</summary>
[Category("AwsEvaluationSuite")]
public sealed class FifoQueueMovesAnExactMultibyteMessageAndTheDeletedFifoQueueIsGone
{
    private const string QueueNotFound = "AWS.SimpleQueueService.NonExistentQueue";
    private const string QueueNotFoundMsg = "The specified queue does not exist.";
    // A deleted queue can keep answering for a while and the counters converge asynchronously.
    private const double GoneWaitS = 40.0;
    private const double DrainWaitS = 40.0;
    private const double ReceiveWaitS = 40.0;

    // Tab, LF, CR, Latin-1, BMP CJK, a private-use char and astral emoji: every range SQS admits.
    private const string Piece = "\tline\nnext\räß€中文�\U0001F600\U0010FFFD|";

    /// <summary>
    /// A FIFO queue moves one multibyte message spanning every admitted character range byte-exact
    /// (MD5 of its UTF-8, MessageId, SequenceNumber, group and dedup ids), then the deleted FIFO queue
    /// is gone: GetQueueUrl by name and ReceiveMessage on its URL answer NonExistentQueue.
    ///
    /// Reference: https://docs.aws.amazon.com/AWSSimpleQueueService/latest/APIReference/API_SendMessage.html
    /// </summary>
    [Test]
    [DisplayName("sqs.fifo-queue-moves-an-exact-multibyte-message-and-the-deleted-fifo-queue-is-gone")]
    [Timeout(FidelityContext.BudgetMilliseconds)]
    [KnownFidelityGap(FidelityGap.QueryErrorCodes, "GetQueueUrl for the deleted FIFO queue: expected AWS.SimpleQueueService.NonExistentQueue, got 'QueueDoesNotExist' after 40s")]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sqs = ctx.Sqs;
        var name = FidelityContext.UniqueName("sim2real-behav-") + ".fifo";
        var url = (await sqs.CreateQueueAsync(new CreateQueueRequest
        {
            QueueName = name, Attributes = new() { ["FifoQueue"] = "true", ["ContentBasedDeduplication"] = "false" },
        })).QueueUrl;
        ctx.OnCleanup(() => DeleteQueueIfPresent(sqs, url));

        var body = string.Concat(Enumerable.Repeat(Piece, 97));
        var group = "grp-" + name[^12..^5];
        var dedup = "dd-" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(body)))[..40];
        var sent = await sqs.SendMessageAsync(new SendMessageRequest
        {
            QueueUrl = url, MessageBody = body, MessageGroupId = group, MessageDeduplicationId = dedup,
        });
        var wantMd5 = Convert.ToHexStringLower(MD5.HashData(Encoding.UTF8.GetBytes(body)));
        Ensure(sent.MD5OfMessageBody == wantMd5,
            $"SendMessage MD5OfMessageBody '{sent.MD5OfMessageBody}', expected the MD5 of the UTF-8 body '{wantMd5}'");
        var received = await PollUntil(
            async () => (await sqs.ReceiveMessageAsync(new ReceiveMessageRequest
            {
                QueueUrl = url,
                MaxNumberOfMessages = 1,
                WaitTimeSeconds = 5,
                MessageSystemAttributeNames =
                [
                    "MessageGroupId",
                    "MessageDeduplicationId",
                    "SequenceNumber",
                ],
            })).Messages ?? [],
            msgs => msgs.Count == 1,
            timeoutS: ReceiveWaitS,
            intervalS: 1,
            description: "the sent message being received",
            repr: msgs => $"{msgs.Count} messages");
        var msg = received[0];
        Ensure(msg.Body == body,
            $"received body of {msg.Body.Length} chars differs from the {body.Length}-char body sent");
        Ensure(msg.MD5OfBody == wantMd5, $"MD5OfBody '{msg.MD5OfBody}', expected '{wantMd5}'");
        Ensure(msg.MessageId == sent.MessageId,
            $"received MessageId '{msg.MessageId}', SendMessage returned '{sent.MessageId}'");
        var attrs = msg.Attributes!;
        var got = (
            attrs.GetValueOrDefault("MessageGroupId"),
            attrs.GetValueOrDefault("MessageDeduplicationId"),
            attrs.GetValueOrDefault("SequenceNumber"));
        var want = (group, dedup, sent.SequenceNumber ?? throw new KeyNotFoundException("SequenceNumber"));
        Ensure(got == want,
            $"(MessageGroupId, MessageDeduplicationId, SequenceNumber) received {got}, expected {want}");

        await sqs.DeleteMessageAsync(new DeleteMessageRequest { QueueUrl = url, ReceiptHandle = msg.ReceiptHandle });
        await PollUntil(
            async () => (await sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest
            {
                QueueUrl = url,
                AttributeNames = ["ApproximateNumberOfMessages", "ApproximateNumberOfMessagesNotVisible"],
            })).Attributes!,
            a => a.GetValueOrDefault("ApproximateNumberOfMessages") == "0"
                && a.GetValueOrDefault("ApproximateNumberOfMessagesNotVisible") == "0",
            timeoutS: DrainWaitS,
            intervalS: 2,
            description: "the queue counters draining to 0",
            repr: a => "{" + string.Join(", ", a.Select(kv => $"'{kv.Key}': '{kv.Value}'")) + "}");

        await sqs.DeleteQueueAsync(url);
        var byName = await PollUntilCode(
            () => sqs.GetQueueUrlAsync(new GetQueueUrlRequest { QueueName = name }),
            QueueNotFound,
            timeoutS: GoneWaitS,
            intervalS: 5,
            what: "GetQueueUrl for the deleted FIFO queue");
        AssertNotFound(byName, "GetQueueUrl for the deleted FIFO queue");
        await PollUntil(
            () => ListUrls(sqs, name[..^5]),
            urls => !urls.Contains(url),
            timeoutS: GoneWaitS,
            intervalS: 5,
            description: "the deleted FIFO queue leaving ListQueues",
            repr: urls => $"[{string.Join(", ", urls)}]");
        var deadReceive = await PollUntilCode(
            () => sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = url, MaxNumberOfMessages = 1, WaitTimeSeconds = 0 }),
            QueueNotFound,
            timeoutS: GoneWaitS,
            intervalS: 5,
            what: "ReceiveMessage on the deleted FIFO queue");
        AssertNotFound(deadReceive, "ReceiveMessage on the deleted FIFO queue");
    }

    private static async Task<T> PollUntil<T>(Func<Task<T>> probe, Func<T, bool> predicate, double timeoutS, double intervalS, string description, Func<T, string> repr)
    {
        var deadline = Stopwatch.StartNew();
        while (true)
        {
            var last = await probe();
            if (predicate(last))
            {
                return last;
            }

            if (deadline.Elapsed.TotalSeconds >= timeoutS)
            {
                throw Fail($"{description} not observed within {timeoutS:F0}s; last saw {repr(last)}");
            }

            await Task.Delay(TimeSpan.FromSeconds(intervalS));
        }
    }

    /// <summary>Poll fn until it fails with code; return that error response. FAILS on the deadline.</summary>
    private static async Task<AwsError> PollUntilCode(Func<Task> fn, string code, double timeoutS, double intervalS, string what)
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
                seen = error.Code ?? throw new KeyNotFoundException("Code");
                if (seen == code)
                {
                    return error;
                }
            }

            if (deadline.Elapsed.TotalSeconds >= timeoutS)
            {
                throw Fail($"{what}: expected {code}, got '{seen}' after {timeoutS:F0}s");
            }

            await Task.Delay(TimeSpan.FromSeconds(intervalS));
        }
    }

    /// <summary>HTTP 400 with the exact NonExistentQueue message.</summary>
    private static void AssertNotFound(AwsError response, string what)
    {
        var status = (int)response.StatusCode;
        Ensure(response.StatusCode == HttpStatusCode.BadRequest, $"{what} answered HTTP {status}, expected 400");
        var message = response.Message;
        Ensure(message == QueueNotFoundMsg,
            $"{what} answered message '{message}', expected '{QueueNotFoundMsg}'");
    }

    private static async Task<List<string>> ListUrls(AmazonSQSClient sqs, string prefix)
    {
        List<string> urls = [];
        string? token = null;
        while (true)
        {
            var request = new ListQueuesRequest { QueueNamePrefix = prefix };
            if (!string.IsNullOrEmpty(token))
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
        try
        {
            await sqs.DeleteQueueAsync(url);
        }
        catch (AmazonServiceException exc) when (exc.ErrorCode == QueueNotFound)
        {
        }
    }
}
