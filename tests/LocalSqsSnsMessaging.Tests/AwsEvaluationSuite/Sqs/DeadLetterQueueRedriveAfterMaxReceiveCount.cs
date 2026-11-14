// Translated from behavioral/sqs/check_dead_letter_queue_redrive_after_max_receive_count.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Text.Json;
using Amazon.SQS;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sqs;

/// <summary>sqs.dead-letter-queue-redrive-after-max-receive-count</summary>
[Category("AwsEvaluationSuite")]
public sealed class DeadLetterQueueRedriveAfterMaxReceiveCount
{
    /// <summary>
    /// A message received maxReceiveCount times without being deleted is moved to the configured
    /// dead-letter queue instead of being redelivered from the source queue, whose visible counter
    /// then polls to 0.
    /// </summary>
    [Test]
    [DisplayName("sqs.dead-letter-queue-redrive-after-max-receive-count")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sqs = ctx.Sqs;
        var dlqUrl = (await sqs.CreateQueueAsync(new CreateQueueRequest { QueueName = FidelityContext.UniqueName("sim2real-sqs-dlq-") })).QueueUrl;
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(dlqUrl));
        var dlqArn = (await sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest { QueueUrl = dlqUrl, AttributeNames = ["QueueArn"] }))
            .Attributes!["QueueArn"];

        var redrivePolicy = PyDumps(("deadLetterTargetArn", dlqArn), ("maxReceiveCount", 1));
        var srcUrl = (await sqs.CreateQueueAsync(new CreateQueueRequest
        {
            QueueName = FidelityContext.UniqueName("sim2real-sqs-dlq-src-"),
            Attributes = new() { ["VisibilityTimeout"] = "30", ["RedrivePolicy"] = redrivePolicy },
        })).QueueUrl;
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(srcUrl));

        const string body = "redrive-me-to-dlq";
        await sqs.SendMessageAsync(new SendMessageRequest { QueueUrl = srcUrl, MessageBody = body });

        // first receive: consumes the message once (ReceiveCount becomes 1, already at
        // maxReceiveCount) but we deliberately do NOT delete it.
        var first = await PollReceiveOne(sqs, srcUrl, timeoutSeconds: 20.0);
        Ensure(first.Body == body, $"expected {body} on first receive, got {first.Body}");

        // re-expose it immediately instead of waiting out the visibility timeout: this is the
        // point at which SQS evaluates maxReceiveCount (1), which the first receive already met,
        // so the next active attempt to redeliver from the source must be routed to the DLQ
        // instead of being handed back here.
        await sqs.ChangeMessageVisibilityAsync(new ChangeMessageVisibilityRequest
        {
            QueueUrl = srcUrl,
            ReceiptHandle = first.ReceiptHandle,
            VisibilityTimeout = 0,
        });

        var redeliveredFromSrc = false;
        for (var i = 0; i < 5; i++)
        {
            var resp = await sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = srcUrl, MaxNumberOfMessages = 1, WaitTimeSeconds = 2 });
            foreach (var _ in resp.Messages ?? [])
            {
                redeliveredFromSrc = true;
            }
        }

        Ensure(!redeliveredFromSrc,
            $"expected {body} to be routed to the DLQ once maxReceiveCount=1 was exceeded, but " +
            "it was redelivered from the source queue instead");

        // the message should now show up in the DLQ instead of the source queue. Poll for it,
        // allowing generous time for the asynchronous redrive-move to complete.
        var dlqMsg = await PollReceiveOne(sqs, dlqUrl, timeoutSeconds: 90.0);
        Ensure(dlqMsg.Body == body, $"expected {body} in the DLQ, got {dlqMsg.Body}");
        await sqs.DeleteMessageAsync(new DeleteMessageRequest { QueueUrl = dlqUrl, ReceiptHandle = dlqMsg.ReceiptHandle });

        // the source queue's visible counter polls to 0 once the redrive move settles
        await PollQueueCounters(ctx, sqs, srcUrl, new() { ["ApproximateNumberOfMessages"] = "0" });
    }

    /// <summary>Python's <c>json.dumps</c> of a flat dict: <c>{"k": v, "k2": v2}</c>, with its default separators.</summary>
    private static string PyDumps(params (string Key, object Value)[] items)
        => "{" + string.Join(", ", items.Select(kv => $"{JsonSerializer.Serialize(kv.Key)}: {JsonSerializer.Serialize(kv.Value)}")) + "}";

    /// <summary>A poll's timeout: cap_s, bounded by the runner's remaining time less 5 s.</summary>
    private static double BudgetSeconds(FidelityContext ctx, double capSeconds)
    {
        var left = ctx.TimeLeftSeconds;
        return Math.Max(1.0, Math.Min(capSeconds, left - 5.0));
    }

    /// <summary>
    /// Poll GetQueueAttributes until every named counter equals its expected string value.
    ///
    /// The ApproximateNumberOf* counters may lag up to a minute after producers stop
    /// (SQS GetQueueAttributes docs), so a single read is not a verdict.
    /// </summary>
    private static async Task<Dictionary<string, string>> PollQueueCounters(
        FidelityContext ctx, AmazonSQSClient sqs, string queueUrl, Dictionary<string, string> expected, double capSeconds = 90.0)
    {
        var budgetSeconds = BudgetSeconds(ctx, capSeconds);
        var start = FidelityTime.StartNew();
        var stop = Math.Min(capSeconds, budgetSeconds);
        while (true)
        {
            var attrs = (await sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest { QueueUrl = queueUrl, AttributeNames = [.. expected.Keys] })).Attributes!;
            var observed = expected.Keys.ToDictionary(name => name, name => attrs.GetValueOrDefault(name));
            if (observed.All(kv => kv.Value == expected[kv.Key]))
            {
                return attrs;
            }

            var observedText = JsonSerializer.Serialize(observed);
            if (start.Elapsed.TotalSeconds >= stop)
            {
                if (budgetSeconds < capSeconds)
                {
                    throw new TimeoutException(
                        $"the check's time budget ran out after {start.Elapsed.TotalSeconds:F1}s, before the " +
                        $"{capSeconds}s counter wait ended (last observed {observedText}); not graded");
                }

                throw Fail($"queue counters never reached {JsonSerializer.Serialize(expected)}; last observed {observedText}");
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(3));
        }
    }

    private static async Task<Message> PollReceiveOne(AmazonSQSClient sqs, string url, double timeoutSeconds = 20.0)
    {
        var deadline = FidelityTime.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(timeoutSeconds))
        {
            var resp = await sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = url, MaxNumberOfMessages = 1, WaitTimeSeconds = 3 });
            var messages = resp.Messages ?? [];
            if (messages.Count > 0)
            {
                return messages[0];
            }
        }

        throw Fail($"no message received from {url} within {timeoutSeconds}s");
    }
}
