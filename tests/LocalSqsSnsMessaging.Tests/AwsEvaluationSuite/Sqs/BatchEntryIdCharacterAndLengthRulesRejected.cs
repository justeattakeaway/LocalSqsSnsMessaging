// Translated from behavioral/sqs/check_batch_entry_id_character_and_length_rules_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Diagnostics;
using Amazon.SQS;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sqs;

/// <summary>sqs.batch-entry-id-character-and-length-rules-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class BatchEntryIdCharacterAndLengthRulesRejected
{
    private static readonly Dictionary<string, string> Tags = new() { ["sim2real"] = "batch-entry-id-character-and-length-rules-rejected" };
    private const string BadId = "AWS.SimpleQueueService.InvalidBatchEntryId";
    private const string BadIdMsg = "A batch entry id can only contain alphanumeric characters, hyphens and " +
                                    "underscores. It can be at most 80 letters long.";
    private static readonly string Id80 = "Az09-_" + new string('x', 74);

    /// <summary>
    /// Batch entry Ids outside 1-80 alphanumeric/hyphen/underscore chars get InvalidBatchEntryId on
    /// all three batch ops, request-wide with no partial effect; a valid 80-char Id is accepted.
    /// </summary>
    [Test]
    [DisplayName("sqs.batch-entry-id-character-and-length-rules-rejected")]
    [Timeout(FidelityContext.BudgetMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sqs = ctx.Sqs;
        var url = (await sqs.CreateQueueAsync(new CreateQueueRequest
        {
            QueueName = FidelityContext.UniqueName("sim2real-behav-batchid-"),
            Tags = new(Tags),
        })).QueueUrl;
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(url));

        var refusedBody = FidelityContext.UniqueName("refused-");
        foreach (var bad in new[] { "a.b", "a b", "", new string('a', 81) })
        {
            var err = await Error(() => sqs.SendMessageBatchAsync(new SendMessageBatchRequest
            {
                QueueUrl = url,
                Entries =
                [
                    new SendMessageBatchRequestEntry { Id = bad, MessageBody = refusedBody },
                    new SendMessageBatchRequestEntry { Id = "ok", MessageBody = refusedBody },
                ],
            }));
            Ensure((err.Code, err.Message) == (BadId, BadIdMsg),
                $"SendMessageBatch with entry Id '{bad}' ({bad.Length} chars) answered {err}");
        }

        var stray = await ReceiveAll(sqs, url, 1);
        Ensure(stray.Count == 0,
            $"refused SendMessageBatch requests enqueued {stray.Count} message(s); the valid sibling " +
            "entry must not be sent when another entry's Id is invalid");

        var sent = await sqs.SendMessageBatchAsync(new SendMessageBatchRequest
        {
            QueueUrl = url,
            Entries = [new SendMessageBatchRequestEntry { Id = Id80, MessageBody = "kept" }],
        });
        Ensure((sent.Successful ?? []).Select(e => e.Id).SequenceEqual([Id80]),
            $"SendMessageBatch with an 80-character Id answered {sent}");
        var held = await ReceiveAll(sqs, url, 1);
        Ensure(held.Select(m => m.Body).SequenceEqual(["kept"]), $"received [{string.Join(", ", held.Select(m => m.Body))}]");
        var handle = held[0].ReceiptHandle;

        var err2 = await Error(() => sqs.ChangeMessageVisibilityBatchAsync(new ChangeMessageVisibilityBatchRequest
        {
            QueueUrl = url,
            Entries = [new ChangeMessageVisibilityBatchRequestEntry { Id = "a b", ReceiptHandle = handle, VisibilityTimeout = 0 }],
        }));
        Ensure((err2.Code, err2.Message) == (BadId, BadIdMsg),
            $"ChangeMessageVisibilityBatch with Id 'a b' answered {err2}");
        var err3 = await Error(() => sqs.DeleteMessageBatchAsync(new DeleteMessageBatchRequest
        {
            QueueUrl = url,
            Entries = [new DeleteMessageBatchRequestEntry { Id = new string('a', 81), ReceiptHandle = handle }],
        }));
        Ensure((err3.Code, err3.Message) == (BadId, BadIdMsg),
            $"DeleteMessageBatch with an 81-character Id answered {err3}");
        var early = await sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = url, MaxNumberOfMessages = 10, WaitTimeSeconds = 3 });
        Ensure((early.Messages ?? []).Count == 0,
            "the refused visibility batch (VisibilityTimeout 0) released the message anyway");

        // Positive control: the same handle, under an 80-character Id, releases the message, which
        // comes back -- so the refused delete deleted nothing -- and a valid delete removes it.
        var resp = await sqs.ChangeMessageVisibilityBatchAsync(new ChangeMessageVisibilityBatchRequest
        {
            QueueUrl = url,
            Entries = [new ChangeMessageVisibilityBatchRequestEntry { Id = Id80, ReceiptHandle = handle, VisibilityTimeout = 0 }],
        });
        Ensure((resp.Successful ?? []).Select(e => e.Id).SequenceEqual([Id80]),
            $"ChangeMessageVisibilityBatch with an 80-character Id answered {resp}");
        var back = await ReceiveAll(sqs, url, 1);
        Ensure(back.Select(m => m.Body).SequenceEqual(["kept"]),
            $"after the refused delete and a valid release the queue delivered " +
            $"[{string.Join(", ", back.Select(m => m.Body))}]; the refused DeleteMessageBatch must not delete");
        var resp2 = await sqs.DeleteMessageBatchAsync(new DeleteMessageBatchRequest
        {
            QueueUrl = url,
            Entries = [new DeleteMessageBatchRequestEntry { Id = Id80, ReceiptHandle = back[0].ReceiptHandle }],
        });
        Ensure((resp2.Successful ?? []).Select(e => e.Id).SequenceEqual([Id80]),
            $"DeleteMessageBatch with an 80-character Id answered {resp2}");
    }

    /// <summary>The botocore Error dict <c>fn</c> raises. An unexpected success raises, naming the result.</summary>
    private static async Task<AwsError> Error<T>(Func<Task<T>> fn)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        return error ?? throw Fail($"expected the batch to be refused, but it succeeded: {result}");
    }

    private static async Task<List<Message>> ReceiveAll(AmazonSQSClient sqs, string url, int want, int visibility = 120)
    {
        var got = new Dictionary<string, Message>();
        var deadline = Stopwatch.StartNew();
        while (got.Count < want && deadline.Elapsed < TimeSpan.FromSeconds(30))
        {
            foreach (var m in (await sqs.ReceiveMessageAsync(new ReceiveMessageRequest
            {
                QueueUrl = url, MaxNumberOfMessages = 10, WaitTimeSeconds = 2, VisibilityTimeout = visibility,
            })).Messages ?? [])
            {
                got[m.MessageId] = m;
            }
        }

        return [.. got.Values];
    }
}
