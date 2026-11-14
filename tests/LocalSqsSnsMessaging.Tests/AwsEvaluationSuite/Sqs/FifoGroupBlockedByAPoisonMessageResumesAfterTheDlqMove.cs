// Translated from behavioral/sqs/check_fifo_group_blocked_by_a_poison_message_resumes_after_the_dlq_move.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Diagnostics;
using System.Text.Json;
using Amazon.SQS;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sqs;

/// <summary>sqs.fifo-group-blocked-by-a-poison-message-resumes-after-the-dlq-move</summary>
[Category("AwsEvaluationSuite")]
public sealed class FifoGroupBlockedByAPoisonMessageResumesAfterTheDlqMove
{
    private const int MaxReceiveCount = 1; // the poison message moves after its 1st delivery; keeps the check cheap
    private const double MoveSeconds = 30.0; // the redrive is sub-second on AWS; generous headroom
    private const double SuccessorSeconds = 30.0; // the head lands in the DLQ sub-second on AWS; generous headroom
    private const int AbsenceMultiplier = 3; // hold the blocked-window absence for >=3x the measured delivery latency

    private static readonly Dictionary<string, string> Tags = new()
    {
        ["sim2real:managed"] = "true",
        ["sim2real"] = "fifo-poison-unblocks-group",
    };

    /// <summary>
    /// A FIFO group blocked by an in-flight poison head resumes at its successor once the head is
    /// redriven: the head reaches the FIFO DLQ with its group id, the source is left empty, and the
    /// over-count head is never handed back to a client.
    ///
    /// Reference: https://docs.aws.amazon.com/AWSSimpleQueueService/latest/APIReference/API_ReceiveMessage.html
    /// Reference: https://docs.aws.amazon.com/AWSSimpleQueueService/latest/SQSDeveloperGuide/sqs-configure-dead-letter-queue-redrive.html
    /// </summary>
    [Test]
    [DisplayName("sqs.fifo-group-blocked-by-a-poison-message-resumes-after-the-dlq-move")]
    [Timeout(FidelityContext.BudgetMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sqs = ctx.Sqs;
        var fifoAttrs = new Dictionary<string, string> { ["FifoQueue"] = "true", ["ContentBasedDeduplication"] = "true" };

        var dlqUrl = (await sqs.CreateQueueAsync(new CreateQueueRequest
        {
            QueueName = FidelityContext.UniqueName("sim2real-behav-dlq-") + ".fifo",
            Attributes = new(fifoAttrs),
            Tags = new(Tags),
        })).QueueUrl;
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(dlqUrl));

        var srcAttrs = new Dictionary<string, string>(fifoAttrs)
        {
            // 60s so the poison message stays genuinely in flight for the whole blocked window below; the
            // check never waits it out -- the redelivery that triggers the move is forced with CMV(0).
            ["VisibilityTimeout"] = "60",
            ["RedrivePolicy"] = PyDumps(("deadLetterTargetArn", await QueueArn(sqs, dlqUrl)), ("maxReceiveCount", MaxReceiveCount)),
        };
        var srcUrl = (await sqs.CreateQueueAsync(new CreateQueueRequest
        {
            QueueName = FidelityContext.UniqueName("sim2real-behav-src-") + ".fifo",
            Attributes = srcAttrs,
            Tags = new(Tags),
        })).QueueUrl;
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(srcUrl));

        var token = Guid.NewGuid().ToString("N")[..8];
        var groupId = $"grp-{token}";
        var poisonBody = $"poison-head-{token}";
        var successorBody = $"successor-{token}";

        // Program order inside one message group IS the delivery order SQS promises.
        await sqs.SendMessageAsync(new SendMessageRequest { QueueUrl = srcUrl, MessageBody = poisonBody, MessageGroupId = groupId });
        await sqs.SendMessageAsync(new SendMessageRequest { QueueUrl = srcUrl, MessageBody = successorBody, MessageGroupId = groupId });

        // The head's first-delivery latency bounds the blocked window below.
        var (poison, latencySeconds) = await AwaitBody(sqs, srcUrl, poisonBody, timeoutSeconds: 40.0, what: "the group head's first delivery");
        Ensure(poison.Attributes!["ApproximateReceiveCount"] == "1",
            "the group head's first delivery reported ApproximateReceiveCount=" +
            $"{poison.Attributes["ApproximateReceiveCount"]}, expected '1'");
        Ensure(poison.Attributes["MessageGroupId"] == groupId,
            "the delivered head carries MessageGroupId=" +
            $"{poison.Attributes.GetValueOrDefault("MessageGroupId")}, expected {groupId}");
        var blockedWindowSeconds = Math.Max(AbsenceMultiplier * latencySeconds, 3.0);

        // Positive control: blocking is in effect, so a later successor delivery is due to the redrive.
        var blocked = Stopwatch.StartNew();
        while (blocked.Elapsed.TotalSeconds < blockedWindowSeconds)
        {
            var leaked = (await Receive(sqs, srcUrl)).Where(m => m.Body == successorBody).ToList();
            Ensure(leaked.Count == 0,
                $"the successor {successorBody} was delivered while the group head " +
                $"{poisonBody} was still in flight, breaking per-group ordering. The head's own " +
                $"first delivery took {latencySeconds:F2}s, so the {blockedWindowSeconds:F1}s window is not " +
                "merely impatience");
        }

        // Force the redelivery that exceeds maxReceiveCount.
        await sqs.ChangeMessageVisibilityAsync(new ChangeMessageVisibilityRequest { QueueUrl = srcUrl, ReceiptHandle = poison.ReceiptHandle, VisibilityTimeout = 0 });
        // AWS redirects the over-count head to the DLQ without ever returning it to a client.
        var (successor, poisonRedeliveries) = await DriveSourceUntilSuccessor(
            sqs,
            srcUrl,
            poisonBody: poisonBody,
            successorBody: successorBody,
            timeoutSeconds: MoveSeconds);
        Ensure(poisonRedeliveries == 0,
            $"the poison head was handed back to a ReceiveMessage caller {poisonRedeliveries} time(s) " +
            $"after already exceeding maxReceiveCount={MaxReceiveCount}, instead of being redirected " +
            "to the DLQ at the receive attempt without ever reaching the client (the behaviour measured " +
            "on real AWS); a client-visible over-count redelivery here means the redrive did not engage " +
            "the way it should");
        Ensure(successor.Attributes!["ApproximateReceiveCount"] == "1",
            "the successor's first delivery reported ApproximateReceiveCount=" +
            $"{successor.Attributes["ApproximateReceiveCount"]}, expected '1' -- a higher count " +
            "means it had already been handed out while the head was in flight");
        Ensure(successor.Attributes["MessageGroupId"] == groupId,
            "the successor carries MessageGroupId=" +
            $"{successor.Attributes.GetValueOrDefault("MessageGroupId")}, expected {groupId}");
        await sqs.DeleteMessageAsync(new DeleteMessageRequest { QueueUrl = srcUrl, ReceiptHandle = successor.ReceiptHandle });

        var moved = await AwaitInDlq(
            sqs,
            dlqUrl,
            poisonBody,
            timeoutSeconds: SuccessorSeconds,
            what: $"with maxReceiveCount={MaxReceiveCount} exceeded, the poison head must arrive in " +
                  "the FIFO DLQ rather than merely vanish from the source");
        Ensure(moved.Attributes!["MessageGroupId"] == groupId,
            "the DLQ copy carries MessageGroupId=" +
            $"{moved.Attributes.GetValueOrDefault("MessageGroupId")}, expected the original {groupId}; a " +
            "FIFO redrive must preserve the message group");
        await sqs.DeleteMessageAsync(new DeleteMessageRequest { QueueUrl = dlqUrl, ReceiptHandle = moved.ReceiptHandle });

        // A redrive moves the message; it must not remain on the source.
        var residue = (await Receive(sqs, srcUrl, waitSeconds: 2)).Select(m => m.Body).ToList();
        Ensure(residue.Count == 0,
            $"the source still delivers [{string.Join(", ", residue)}] after the head was redriven and the successor was " +
            "consumed; a redrive MOVES the message rather than copying it");
    }

    private static async Task<string> QueueArn(AmazonSQSClient sqs, string url)
        => (await sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest { QueueUrl = url, AttributeNames = ["QueueArn"] })).Attributes!["QueueArn"];

    // One at a time: one ReceiveMessage can return several messages of a FIFO group, and with
    // maxReceiveCount=1 re-exposing an extra one would redrive it to the DLQ too.
    private static async Task<List<Message>> Receive(AmazonSQSClient sqs, string url, int waitSeconds = 1)
        => (await sqs.ReceiveMessageAsync(new ReceiveMessageRequest
        {
            QueueUrl = url,
            MaxNumberOfMessages = 1,
            WaitTimeSeconds = waitSeconds,
            MessageSystemAttributeNames = ["All"],
        })).Messages ?? [];

    /// <summary>
    /// Poll until `body` is delivered; return (message, latency_s), raise on the deadline.
    ///
    /// Other messages are re-exposed with visibility 0, since in-flight ones block their FIFO group.
    /// </summary>
    private static async Task<(Message Message, double LatencySeconds)> AwaitBody(AmazonSQSClient sqs, string url, string body, double timeoutSeconds, string what)
    {
        var start = Stopwatch.StartNew();
        while (start.Elapsed.TotalSeconds < timeoutSeconds)
        {
            Message? found = null;
            double latency = 0;
            foreach (var msg in await Receive(sqs, url))
            {
                if (msg.Body == body)
                {
                    if (found is null)
                    {
                        (found, latency) = (msg, start.Elapsed.TotalSeconds);
                    }

                    continue;
                }

                await sqs.ChangeMessageVisibilityAsync(new ChangeMessageVisibilityRequest { QueueUrl = url, ReceiptHandle = msg.ReceiptHandle, VisibilityTimeout = 0 });
            }

            if (found is not null)
            {
                return (found, latency);
            }
        }

        throw Fail($"{what}: {body} was not delivered from {url} within {timeoutSeconds}s");
    }

    /// <summary>
    /// Poll the source until the group hands out the successor; return it and poison redeliveries.
    ///
    /// SQS applies the redrive policy at a receive attempt, so the source must keep being polled.
    /// </summary>
    private static async Task<(Message Successor, int PoisonRedeliveries)> DriveSourceUntilSuccessor(
        AmazonSQSClient sqs, string srcUrl, string poisonBody, string successorBody, double timeoutSeconds)
    {
        var deadline = Stopwatch.StartNew();
        var poisonRedeliveries = 0;
        while (deadline.Elapsed.TotalSeconds < timeoutSeconds)
        {
            Message? found = null;
            foreach (var msg in await Receive(sqs, srcUrl))
            {
                if (msg.Body == successorBody)
                {
                    found ??= msg;
                    continue;
                }

                Ensure(msg.Body == poisonBody,
                    $"the source delivered an unexpected body {msg.Body}; only {poisonBody} " +
                    $"and {successorBody} were ever sent to this queue");
                poisonRedeliveries++;
                await sqs.ChangeMessageVisibilityAsync(new ChangeMessageVisibilityRequest { QueueUrl = srcUrl, ReceiptHandle = msg.ReceiptHandle, VisibilityTimeout = 0 });
            }

            if (found is not null)
            {
                return (found, poisonRedeliveries);
            }
        }

        throw Fail(
            $"the message group never resumed: {successorBody} was not delivered within " +
            $"{timeoutSeconds}s of the poison head becoming over-count ({poisonRedeliveries} poison " +
            "redeliveries observed). A group that stays blocked here is stalled forever");
    }

    /// <summary>Poll the DLQ until `body` arrives; raise on the deadline.</summary>
    private static async Task<Message> AwaitInDlq(AmazonSQSClient sqs, string dlqUrl, string body, double timeoutSeconds, string what)
    {
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed.TotalSeconds < timeoutSeconds)
        {
            Message? found = null;
            foreach (var msg in await Receive(sqs, dlqUrl))
            {
                if (msg.Body == body)
                {
                    found ??= msg;
                    continue;
                }

                await sqs.ChangeMessageVisibilityAsync(new ChangeMessageVisibilityRequest { QueueUrl = dlqUrl, ReceiptHandle = msg.ReceiptHandle, VisibilityTimeout = 0 });
            }

            if (found is not null)
            {
                return found;
            }
        }

        throw Fail($"{what}: {body} did not reach the DLQ within {timeoutSeconds}s");
    }

    /// <summary>Python's <c>json.dumps</c> of a flat dict, with its default separators.</summary>
    private static string PyDumps(params (string Key, object Value)[] items)
        => "{" + string.Join(", ", items.Select(kv => $"{JsonSerializer.Serialize(kv.Key)}: {JsonSerializer.Serialize(kv.Value)}")) + "}";
}
