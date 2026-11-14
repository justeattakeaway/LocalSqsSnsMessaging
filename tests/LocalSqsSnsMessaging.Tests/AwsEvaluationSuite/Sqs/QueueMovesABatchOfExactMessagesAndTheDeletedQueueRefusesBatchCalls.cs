// Translated from behavioral/sqs/check_queue_moves_a_batch_of_exact_messages_and_the_deleted_queue_refuses_batch_calls.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Amazon.SQS;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sqs;

/// <summary>sqs.queue-moves-a-batch-of-exact-messages-and-the-deleted-queue-refuses-batch-calls</summary>
[Category("AwsEvaluationSuite")]
public sealed class QueueMovesABatchOfExactMessagesAndTheDeletedQueueRefusesBatchCalls
{
    private const string QueueNotFound = "AWS.SimpleQueueService.NonExistentQueue";
    private const string QueueNotFoundMsg = "The specified queue does not exist.";

    // A deleted queue can keep answering for a while and the counters converge asynchronously.
    private const double GoneWaitSeconds = 40.0;
    private const double DrainWaitSeconds = 40.0;
    private const double ReceiveWaitSeconds = 40.0;

    // Three bodies of different sizes and alphabets, so no one length or content can be assumed.
    private static readonly Dictionary<string, string> Bodies = new()
    {
        ["e0"] = "z",
        ["e1"] = string.Concat(Enumerable.Repeat("Grüße-", 171)),
        ["e2"] = string.Concat(Enumerable.Repeat("0123456789abcdef", 4096)),
    };

    private static async Task<T> PollUntil<T>(Func<Task<T>> probe, Func<T, bool> predicate, double timeoutSeconds, double intervalSeconds, string description, Func<T, string>? show = null)
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
                throw Fail($"{description} not observed within {timeoutSeconds:F0}s; last saw {(show is null ? last?.ToString() : show(last))}");
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

    /// <summary>The NonExistentQueue error response of one call; any other outcome fails.</summary>
    private static async Task<AwsError> Error(Func<Task> fn, string what)
    {
        var error = await FidelityContext.CaptureError(fn);
        if (error is not null)
        {
            Ensure(error.Code == QueueNotFound, $"{what}: expected {QueueNotFound}, got '{error.Code}'");
            return error;
        }

        throw Fail($"{what}: expected {QueueNotFound}, the call succeeded");
    }

    /// <summary>HTTP 400 with the exact NonExistentQueue message.</summary>
    private static void AssertNotFound(AwsError response, string what)
    {
        var status = (int)response.StatusCode;
        Ensure(status == 400, $"{what} answered HTTP {status}, expected 400");
        var message = response.Message;
        Ensure(message == QueueNotFoundMsg, $"{what} answered message '{message}', expected '{QueueNotFoundMsg}'");
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
    /// SendMessageBatch, ReceiveMessage and DeleteMessageBatch move three differently sized messages
    /// byte-exact (per-entry MD5 and MessageId) and drain the queue; once deleted, the queue reads
    /// NonExistentQueue, leaves the listing and refuses SendMessageBatch and DeleteMessageBatch.
    ///
    /// Reference: https://docs.aws.amazon.com/AWSSimpleQueueService/latest/APIReference/API_SendMessageBatch.html
    /// </summary>
    [Test]
    [DisplayName("sqs.queue-moves-a-batch-of-exact-messages-and-the-deleted-queue-refuses-batch-calls")]
    [Timeout(FidelityContext.BudgetMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sqs = ctx.Sqs;
        var name = FidelityContext.UniqueName("sim2real-behav-");
        var url = (await sqs.CreateQueueAsync(new CreateQueueRequest { QueueName = name, Attributes = new() { ["VisibilityTimeout"] = "120" } })).QueueUrl;
        ctx.OnCleanup(() => DeleteQueueIfPresent(sqs, url));

        var sent = await sqs.SendMessageBatchAsync(new SendMessageBatchRequest
        {
            QueueUrl = url,
            Entries = [.. Bodies.Select(kv => new SendMessageBatchRequestEntry { Id = kv.Key, MessageBody = kv.Value })],
        });
        Ensure((sent.Failed ?? []).Count == 0,
            $"SendMessageBatch failed entries: [{string.Join(", ", (sent.Failed ?? []).Select(f => $"{f.Id}: {f.Code}"))}]");
        var byId = (sent.Successful ?? throw new KeyNotFoundException("Successful")).ToDictionary(e => e.Id);
        Ensure(byId.Keys.Order(StringComparer.Ordinal).SequenceEqual(Bodies.Keys.Order(StringComparer.Ordinal)),
            $"SendMessageBatch succeeded ids [{string.Join(", ", byId.Keys.Order(StringComparer.Ordinal))}], expected [{string.Join(", ", Bodies.Keys.Order(StringComparer.Ordinal))}]");
        var md5 = Bodies.ToDictionary(kv => kv.Key, kv => Convert.ToHexStringLower(MD5.HashData(Encoding.UTF8.GetBytes(kv.Value))));
        foreach (var (i, entry) in byId)
        {
            Ensure(entry.MD5OfMessageBody == md5[i], $"entry {i} MD5OfMessageBody '{entry.MD5OfMessageBody}', expected '{md5[i]}'");
        }

        var want = Bodies.Keys.ToDictionary(i => byId[i].MessageId, i => i);

        var got = new Dictionary<string, Message>();
        var deadline = Stopwatch.StartNew();
        while (got.Count < want.Count && deadline.Elapsed < TimeSpan.FromSeconds(ReceiveWaitSeconds))
        {
            foreach (var m in (await sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = url, MaxNumberOfMessages = 10, WaitTimeSeconds = 5 })).Messages ?? [])
            {
                got[m.MessageId] = m;
            }
        }

        Ensure(got.Keys.Order(StringComparer.Ordinal).SequenceEqual(want.Keys.Order(StringComparer.Ordinal)),
            $"received {got.Count} distinct MessageIds, expected exactly the {want.Count} the batch returned");
        foreach (var (mid, m) in got)
        {
            var i = want[mid];
            Ensure(m.Body == Bodies[i], $"message {i} arrived with a {m.Body.Length}-char body, sent {Bodies[i].Length} chars");
            Ensure(m.MD5OfBody == md5[i], $"message {i} MD5OfBody '{m.MD5OfBody}', expected '{md5[i]}'");
        }

        List<DeleteMessageBatchRequestEntry> Handles() => [.. got.Select(kv => new DeleteMessageBatchRequestEntry { Id = want[kv.Key], ReceiptHandle = kv.Value.ReceiptHandle })];
        var deleted = await sqs.DeleteMessageBatchAsync(new DeleteMessageBatchRequest { QueueUrl = url, Entries = Handles() });
        Ensure((deleted.Failed ?? []).Count == 0,
            $"DeleteMessageBatch failed entries: [{string.Join(", ", (deleted.Failed ?? []).Select(f => $"{f.Id}: {f.Code}"))}]");
        var deletedSuccessful = deleted.Successful ?? throw new KeyNotFoundException("Successful");
        Ensure(deletedSuccessful.Select(e => e.Id).Order(StringComparer.Ordinal).SequenceEqual(Bodies.Keys.Order(StringComparer.Ordinal)),
            $"DeleteMessageBatch succeeded ids [{string.Join(", ", deletedSuccessful.Select(e => e.Id))}], expected [{string.Join(", ", Bodies.Keys.Order(StringComparer.Ordinal))}]");
        await PollUntil(
            async () => (await sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest
            {
                QueueUrl = url,
                AttributeNames = ["ApproximateNumberOfMessages", "ApproximateNumberOfMessagesNotVisible"],
            })).Attributes ?? throw new KeyNotFoundException("Attributes"),
            a => a.GetValueOrDefault("ApproximateNumberOfMessages") == "0" && a.GetValueOrDefault("ApproximateNumberOfMessagesNotVisible") == "0",
            timeoutSeconds: DrainWaitSeconds,
            intervalSeconds: 2,
            description: "the queue counters draining to 0",
            show: a => "{" + string.Join(", ", a.Select(kv => $"{kv.Key}: {kv.Value}")) + "}");

        await sqs.DeleteQueueAsync(new DeleteQueueRequest { QueueUrl = url });
        // The read-only GetQueueAttributes establishes the deletion; the batch calls are then graded once.
        var read = await PollUntilCode(
            () => sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest { QueueUrl = url, AttributeNames = ["QueueArn"] }),
            QueueNotFound,
            timeoutSeconds: GoneWaitSeconds,
            intervalSeconds: 5,
            what: "GetQueueAttributes on the deleted queue");
        AssertNotFound(read, "GetQueueAttributes on the deleted queue");
        await PollUntil(
            () => ListUrls(sqs, name),
            urls => !urls.Contains(url),
            timeoutSeconds: GoneWaitSeconds,
            intervalSeconds: 5,
            description: "the deleted queue leaving ListQueues",
            show: urls => $"[{string.Join(", ", urls)}]");
        var deadSend = await Error(
            () => sqs.SendMessageBatchAsync(new SendMessageBatchRequest
            {
                QueueUrl = url,
                Entries = [new() { Id = "late", MessageBody = "after-delete" }],
            }),
            "SendMessageBatch on the deleted queue");
        AssertNotFound(deadSend, "SendMessageBatch on the deleted queue");
        var deadDelete = await Error(
            () => sqs.DeleteMessageBatchAsync(new DeleteMessageBatchRequest { QueueUrl = url, Entries = Handles() }),
            "DeleteMessageBatch on the deleted queue");
        AssertNotFound(deadDelete, "DeleteMessageBatch on the deleted queue");
    }
}
