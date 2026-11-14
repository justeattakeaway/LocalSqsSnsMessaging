// Translated from behavioral/sqs/check_delete_and_visibility_batches_duplicate_ids_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Text.Json;
using Amazon.SQS;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sqs;

/// <summary>sqs.delete-and-visibility-batches-duplicate-ids-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class DeleteAndVisibilityBatchesDuplicateIdsRejected
{
    private static readonly Dictionary<string, string> Tags = new() { ["sim2real"] = "delete-and-visibility-batches-duplicate-ids-rejected" };
    private const string NotDistinct = "AWS.SimpleQueueService.BatchEntryIdsNotDistinct";
    private const string NotDistinctMsg = "Id dup repeated.";

    /// <summary>
    /// DeleteMessageBatch and ChangeMessageVisibilityBatch with two entries sharing an Id are
    /// refused wholesale with BatchEntryIdsNotDistinct, touching neither message; distinct Ids succeed
    /// in full.
    /// </summary>
    [Test]
    [DisplayName("sqs.delete-and-visibility-batches-duplicate-ids-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sqs = ctx.Sqs;
        var url = (await sqs.CreateQueueAsync(new CreateQueueRequest
        {
            QueueName = FidelityContext.UniqueName("sim2real-behav-batchdup-"),
            Attributes = new() { ["VisibilityTimeout"] = "120" },
            Tags = new(Tags),
        })).QueueUrl;
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(url));

        await sqs.SendMessageBatchAsync(new SendMessageBatchRequest
        {
            QueueUrl = url,
            Entries =
            [
                new() { Id = "s0", MessageBody = "dup-msg-a" },
                new() { Id = "s1", MessageBody = "dup-msg-b" },
            ],
        });
        var held = await Take(sqs, url, 2);
        Ensure(held.Count == 2, $"could take only {held.Count} of 2 messages in flight");
        var byBody = ByBody(held);
        var handleA = byBody["dup-msg-a"].ReceiptHandle;
        var handleB = byBody["dup-msg-b"].ReceiptHandle;

        var err = await Error(() => sqs.DeleteMessageBatchAsync(new DeleteMessageBatchRequest
        {
            QueueUrl = url,
            Entries =
            [
                new() { Id = "dup", ReceiptHandle = handleA },
                new() { Id = "dup", ReceiptHandle = handleB },
            ],
        }));
        Ensure((err.Code, err.Message) == (NotDistinct, NotDistinctMsg),
            $"a duplicate-Id DeleteMessageBatch answered {err}");

        err = await Error(() => sqs.ChangeMessageVisibilityBatchAsync(new ChangeMessageVisibilityBatchRequest
        {
            QueueUrl = url,
            Entries =
            [
                new() { Id = "dup", ReceiptHandle = handleA, VisibilityTimeout = 0 },
                new() { Id = "dup", ReceiptHandle = handleB, VisibilityTimeout = 0 },
            ],
        }));
        Ensure((err.Code, err.Message) == (NotDistinct, NotDistinctMsg),
            $"a duplicate-Id ChangeMessageVisibilityBatch answered {err}");

        // Both messages are still in flight, so an immediate receive sees nothing.
        var leaked = await sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = url, MaxNumberOfMessages = 10, WaitTimeSeconds = 3 });
        Ensure((leaked.Messages ?? []).Count == 0,
            "after the two refused duplicate-Id batches a receive returned " +
            $"[{string.Join(", ", (leaked.Messages ?? []).Select(m => m.Body))}]; the refusals must not have " +
            "deleted or released either message");

        var resp = await sqs.ChangeMessageVisibilityBatchAsync(new ChangeMessageVisibilityBatchRequest
        {
            QueueUrl = url,
            Entries =
            [
                new() { Id = "v0", ReceiptHandle = handleA, VisibilityTimeout = 0 },
                new() { Id = "v1", ReceiptHandle = handleB, VisibilityTimeout = 0 },
            ],
        });
        var ok = (resp.Successful ?? []).Select(e => e.Id).ToHashSet();
        Ensure(ok.SetEquals(["v0", "v1"]) && resp.Failed is null or { Count: 0 },
            $"a distinct-Id ChangeMessageVisibilityBatch answered Successful=[{string.Join(", ", ok)}] " +
            $"Failed={Repr(resp.Failed)}");
        var released = await Take(sqs, url, 2);
        var releasedBodies = released.Select(m => m.Body).ToHashSet();
        Ensure(releasedBodies.SetEquals(["dup-msg-a", "dup-msg-b"]),
            $"after the distinct-Id visibility batch, [{string.Join(", ", releasedBodies.Order(StringComparer.Ordinal))}] came back; " +
            "expected both released messages");

        // The release minted fresh receipt handles; delete with those.
        var byBody2 = ByBody(released);
        var resp2 = await sqs.DeleteMessageBatchAsync(new DeleteMessageBatchRequest
        {
            QueueUrl = url,
            Entries =
            [
                new() { Id = "d0", ReceiptHandle = byBody2["dup-msg-a"].ReceiptHandle },
                new() { Id = "d1", ReceiptHandle = byBody2["dup-msg-b"].ReceiptHandle },
            ],
        });
        ok = (resp2.Successful ?? []).Select(e => e.Id).ToHashSet();
        Ensure(ok.SetEquals(["d0", "d1"]) && resp2.Failed is null or { Count: 0 },
            $"a distinct-Id DeleteMessageBatch answered Successful=[{string.Join(", ", ok)}] " +
            $"Failed={Repr(resp2.Failed)}");
        var empty = await sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = url, MaxNumberOfMessages = 10, WaitTimeSeconds = 3 });
        Ensure((empty.Messages ?? []).Count == 0,
            $"after both deletes a receive returned [{string.Join(", ", (empty.Messages ?? []).Select(m => m.Body))}]");
    }

    /// <summary>The botocore Error dict <paramref name="fn"/> raises. An unexpected success raises, naming the result.</summary>
    private static async Task<AwsError> Error<T>(Func<Task<T>> fn)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is not null)
        {
            return error;
        }

        throw Fail($"expected the batch to be refused, but it succeeded: {Repr(result)}");
    }

    private static async Task<List<Message>> Take(AmazonSQSClient sqs, string url, int n, double timeoutSeconds = 30)
    {
        var held = new Dictionary<string, Message>();
        var deadline = FidelityTime.StartNew();
        while (held.Count < n && deadline.Elapsed.TotalSeconds < timeoutSeconds)
        {
            foreach (var m in (await sqs.ReceiveMessageAsync(new ReceiveMessageRequest
                     {
                         QueueUrl = url,
                         MaxNumberOfMessages = 10,
                         WaitTimeSeconds = 2,
                         VisibilityTimeout = 120,
                     })).Messages ?? [])
            {
                held[m.MessageId] = m;
            }
        }

        return [.. held.Values];
    }

    /// <summary>Python's <c>{m["Body"]: m for m in msgs}</c>: the last message with a body wins.</summary>
    private static Dictionary<string, Message> ByBody(List<Message> msgs)
    {
        var byBody = new Dictionary<string, Message>();
        foreach (var m in msgs)
        {
            byBody[m.Body] = m;
        }

        return byBody;
    }

    private static string Repr(object? value)
    {
        try
        {
            return JsonSerializer.Serialize(value, value?.GetType() ?? typeof(object));
        }
        catch (Exception)
        {
            return value?.ToString() ?? "None";
        }
    }
}
