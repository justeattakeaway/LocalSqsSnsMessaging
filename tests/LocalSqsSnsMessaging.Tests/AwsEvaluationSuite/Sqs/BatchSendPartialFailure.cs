// Translated from behavioral/sqs/check_batch_send_partial_failure.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Diagnostics;
using Amazon.SQS;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sqs;

/// <summary>sqs.batch-send-partial-failure</summary>
[Category("AwsEvaluationSuite")]
public sealed class BatchSendPartialFailure
{
    private const string ErrInvalidParameter = "InvalidParameterValue";

    /// <summary>
    /// SendMessageBatch with one entry whose DelaySeconds exceeds 900 fails only that entry
    /// (InvalidParameterValue, SenderFault) while its two valid siblings still enqueue.
    /// </summary>
    [Test]
    [DisplayName("sqs.batch-send-partial-failure")]
    [Timeout(FidelityContext.BudgetMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sqs = ctx.Sqs;
        var url = (await sqs.CreateQueueAsync(new CreateQueueRequest { QueueName = FidelityContext.UniqueName("sim2real-behav-") })).QueueUrl;
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(url));
        const double drainS = 12;

        List<SendMessageBatchRequestEntry> entries =
        [
            new() { Id = "before", MessageBody = "kept-before" },
            new() { Id = "baddelay", MessageBody = "rejected-delay", DelaySeconds = 901 },
            new() { Id = "after", MessageBody = "kept-after" },
        ];
        var resp = await sqs.SendMessageBatchAsync(new SendMessageBatchRequest { QueueUrl = url, Entries = entries });
        var succ = (resp.Successful ?? []).ToDictionary(e => e.Id);
        var fail = (resp.Failed ?? []).ToDictionary(e => e.Id);

        Ensure(succ.Keys.ToHashSet().SetEquals(["before", "after"]), $"Successful Ids: [{string.Join(", ", succ.Keys.Order(StringComparer.Ordinal))}]");
        Ensure(fail.Keys.ToHashSet().SetEquals(["baddelay"]), $"Failed Ids: [{string.Join(", ", fail.Keys.Order(StringComparer.Ordinal))}]");

        var bad = fail["baddelay"];
        Ensure(bad.Code == ErrInvalidParameter, $"Failed Code: '{bad.Code}'");
        Ensure(bad.SenderFault is true, $"SenderFault must be True for an out-of-range DelaySeconds, got {bad.SenderFault}");

        // An independent receive proves the siblings landed and the oversized entry did not
        // sneak in under a different id.
        var bodies = await Drain(sqs, url, timeoutS: drainS);
        Ensure(bodies.ToHashSet().SetEquals(["kept-before", "kept-after"]),
            $"expected exactly the two valid bodies enqueued, drained [{string.Join(", ", bodies)}]");
    }

    /// <summary>Receive-and-delete every distinct message that shows up inside timeout_s.</summary>
    private static async Task<List<string>> Drain(AmazonSQSClient sqs, string url, double timeoutS)
    {
        var seen = new HashSet<string>();
        var bodies = new List<string>();
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(timeoutS))
        {
            var resp = await sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = url, MaxNumberOfMessages = 10, WaitTimeSeconds = 1 });
            foreach (var msg in resp.Messages ?? [])
            {
                if (seen.Add(msg.MessageId))
                {
                    bodies.Add(msg.Body);
                }

                await sqs.DeleteMessageAsync(new DeleteMessageRequest { QueueUrl = url, ReceiptHandle = msg.ReceiptHandle });
            }
        }

        return bodies;
    }
}
