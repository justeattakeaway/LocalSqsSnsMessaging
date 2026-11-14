// Translated from behavioral/sqs/check_content_based_deduplication_toggled_on_live_fifo_queue.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Diagnostics;
using Amazon.Runtime;
using Amazon.SQS;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sqs;

/// <summary>sqs.content-based-deduplication-toggled-on-live-fifo-queue</summary>
[Category("AwsEvaluationSuite")]
public sealed class ContentBasedDeduplicationToggledOnLiveFifoQueue
{
    private static readonly Dictionary<string, string> Tags = new() { ["sim2real:managed"] = "true", ["sim2real"] = "cbd-toggle" };

    private const string NoDedupIdMessage =
        "The queue should either have ContentBasedDeduplication enabled or MessageDeduplicationId " +
        "provided explicitly";
    // Bound on how long a ContentBasedDeduplication change may take to govern SendMessage.
    private const double ConvergeS = 90.0;

    /// <summary>
    /// Toggling ContentBasedDeduplication on a live FIFO queue: on, id-less sends are legal and
    /// dedupe by body while explicit ids override the hash; off again, id-less sends are refused.
    /// </summary>
    [Test]
    [DisplayName("sqs.content-based-deduplication-toggled-on-live-fifo-queue")]
    [Timeout(FidelityContext.BudgetMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sqs = ctx.Sqs;
        var token = Guid.NewGuid().ToString("N")[..8];
        var url = (await sqs.CreateQueueAsync(new CreateQueueRequest
        {
            QueueName = FidelityContext.UniqueName("sim2real-behav-") + ".fifo",
            Attributes = new() { ["FifoQueue"] = "true", ["ContentBasedDeduplication"] = "false" },
            Tags = new(Tags),
        })).QueueUrl;
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(url));

        var (code, msg) = await SendError(() => sqs.SendMessageAsync(new SendMessageRequest
        {
            QueueUrl = url, MessageBody = $"refused-while-off-{token}", MessageGroupId = "g",
        }));
        Ensure((code, msg) == ("InvalidParameterValue", NoDedupIdMessage),
            $"id-less send with ContentBasedDeduplication off gave ({code}, {msg})");

        await sqs.SetQueueAttributesAsync(new SetQueueAttributesRequest { QueueUrl = url, Attributes = new() { ["ContentBasedDeduplication"] = "true" } });
        var stored = (await sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest
        {
            QueueUrl = url, AttributeNames = ["ContentBasedDeduplication"],
        })).Attributes!.GetValueOrDefault("ContentBasedDeduplication");
        Ensure(stored == "true", $"ContentBasedDeduplication did not read back 'true': '{stored}'");

        var repeated = $"repeated-body-{token}";
        string? firstId = null;
        var deadline = Stopwatch.StartNew();
        while (firstId is null && deadline.Elapsed < TimeSpan.FromSeconds(ConvergeS))
        {
            try
            {
                firstId = (await sqs.SendMessageAsync(new SendMessageRequest { QueueUrl = url, MessageBody = repeated, MessageGroupId = "g" })).MessageId;
            }
            catch (AmazonServiceException exc)
            {
                Ensure(exc.ErrorCode == "InvalidParameterValue",
                    $"id-less send while the switch propagates raised ({exc.ErrorCode}, {exc.Message})");
                await Task.Delay(TimeSpan.FromSeconds(2));
            }
        }

        Ensure(firstId is not null,
            $"an id-less send was still refused {ConvergeS:F0}s after enabling " +
            "ContentBasedDeduplication");

        var again = new List<string>();
        for (var i = 0; i < 2; i++)
        {
            again.Add((await sqs.SendMessageAsync(new SendMessageRequest { QueueUrl = url, MessageBody = repeated, MessageGroupId = "g" })).MessageId);
        }

        Ensure(again.SequenceEqual([firstId, firstId]),
            "re-sending the identical body under content-based dedup must return the original " +
            $"MessageId: first '{firstId}', then [{string.Join(", ", again)}]");

        // An explicit id overrides the content hash: same body, two ids, two messages.
        var explicitBody = $"explicit-ids-body-{token}";
        var ids = new List<string>();
        for (var i = 0; i < 2; i++)
        {
            ids.Add((await sqs.SendMessageAsync(new SendMessageRequest
            {
                QueueUrl = url, MessageBody = explicitBody, MessageGroupId = "g", MessageDeduplicationId = $"{token}-{i}",
            })).MessageId);
        }

        Ensure(ids[0] != ids[1],
            "two sends of one body under two explicit MessageDeduplicationIds were collapsed to " +
            $"MessageId '{ids[0]}'; an explicit id overrides the content hash");

        var drained = await Drain(sqs, url);
        Ensure(drained.SequenceEqual([repeated, explicitBody, explicitBody]),
            "queue must hold the repeated body once, then the explicit-id body twice, in send " +
            $"order: [{string.Join(", ", drained)}]");

        await sqs.SetQueueAttributesAsync(new SetQueueAttributesRequest { QueueUrl = url, Attributes = new() { ["ContentBasedDeduplication"] = "false" } });
        var late = new List<string>();
        (string?, string?)? refused = null;
        deadline = Stopwatch.StartNew();
        while (refused is null && deadline.Elapsed < TimeSpan.FromSeconds(ConvergeS))
        {
            var body = $"after-disable-{late.Count}-{token}";
            try
            {
                await sqs.SendMessageAsync(new SendMessageRequest { QueueUrl = url, MessageBody = body, MessageGroupId = "g" });
                late.Add(body);
                await Task.Delay(TimeSpan.FromSeconds(2));
            }
            catch (AmazonServiceException exc)
            {
                refused = (exc.ErrorCode, exc.Message);
            }
        }

        Ensure(refused == ("InvalidParameterValue", NoDedupIdMessage),
            "id-less send after disabling ContentBasedDeduplication was not refused with the " +
            $"documented error within {ConvergeS:F0}s: {refused?.ToString() ?? "None"}");
        var tail = await Drain(sqs, url);
        Ensure(tail.SequenceEqual(late),
            "only the sends accepted while the switch propagated may be on the queue: " +
            $"[{string.Join(", ", tail)}] vs [{string.Join(", ", late)}]");
    }

    /// <summary>(code, message) of the refusal <c>fn</c> raises; an unexpected success raises, naming it.</summary>
    private static async Task<(string? Code, string? Message)> SendError<T>(Func<Task<T>> fn)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is not null)
        {
            return (error.Code, error.Message);
        }

        throw Fail($"expected the send to be refused, but it succeeded: {result}");
    }

    /// <summary>Receive-and-delete every message until three consecutive empty long polls.</summary>
    private static async Task<List<string>> Drain(AmazonSQSClient sqs, string url)
    {
        var bodies = new List<string>();
        var empties = 0;
        var deadline = Stopwatch.StartNew();
        while (empties < 3 && deadline.Elapsed < TimeSpan.FromSeconds(60))
        {
            var batch = (await sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = url, MaxNumberOfMessages = 10, WaitTimeSeconds = 2 })).Messages ?? [];
            if (batch.Count == 0)
            {
                empties++;
                continue;
            }

            empties = 0;
            foreach (var m in batch)
            {
                bodies.Add(m.Body);
                await sqs.DeleteMessageAsync(new DeleteMessageRequest { QueueUrl = url, ReceiptHandle = m.ReceiptHandle });
            }
        }

        return bodies;
    }
}
