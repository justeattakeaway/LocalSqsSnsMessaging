// Translated from behavioral/sqs/check_dlq_moves_only_the_undeleted_message_after_four_deliveries.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Text.Json;
using Amazon.SQS;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sqs;

/// <summary>sqs.dlq-moves-only-the-undeleted-message-after-four-deliveries</summary>
[Category("AwsEvaluationSuite")]
public sealed class DlqMovesOnlyTheUndeletedMessageAfterFourDeliveries
{
    private const int MaxReceiveCount = 4;
    private const int HealthyDeletedAt = 2; // the healthy message is deleted on this delivery, below the count
    private const double MoveSeconds = 40.0; // bounded wait for the asynchronous source -> DLQ move
    private const double CounterSeconds = 40.0; // bounded wait for the approximate counters to converge

    private static readonly Dictionary<string, string> Tags = new()
    {
        ["sim2real:managed"] = "true",
        ["sim2real"] = "dlq-moves-only-undeleted",
    };

    /// <summary>
    /// With maxReceiveCount=4, a healthy message deleted on its 2nd delivery never moves, while an
    /// undeleted one is delivered with ApproximateReceiveCount 1..4 and only then reaches the DLQ with
    /// its MessageId, body and attribute intact; the DLQ holds nothing but that message.
    ///
    /// Reference: https://docs.aws.amazon.com/AWSSimpleQueueService/latest/SQSDeveloperGuide/sqs-dead-letter-queues.html
    /// </summary>
    [Test]
    [DisplayName("sqs.dlq-moves-only-the-undeleted-message-after-four-deliveries")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sqs = ctx.Sqs;

        var dlqUrl = (await sqs.CreateQueueAsync(new CreateQueueRequest { QueueName = FidelityContext.UniqueName("sim2real-behav-dlq-"), Tags = new(Tags) })).QueueUrl;
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(dlqUrl));
        var dlqArn = await QueueArn(sqs, dlqUrl);

        var srcUrl = (await sqs.CreateQueueAsync(new CreateQueueRequest
        {
            QueueName = FidelityContext.UniqueName("sim2real-behav-src-"),
            Attributes = new()
            {
                // Only a ceiling: every redelivery is forced with ChangeMessageVisibility(0).
                ["VisibilityTimeout"] = "30",
                ["RedrivePolicy"] = PyDumps(("deadLetterTargetArn", dlqArn), ("maxReceiveCount", MaxReceiveCount.ToString())),
            },
            Tags = new(Tags),
        })).QueueUrl;
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(srcUrl));

        var policyText = (await sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest { QueueUrl = srcUrl, AttributeNames = ["RedrivePolicy"] })).Attributes!["RedrivePolicy"];
        using (var policyDoc = JsonDocument.Parse(policyText))
        {
            var policy = policyDoc.RootElement;
            Ensure((policy.GetProperty("deadLetterTargetArn").GetString(), PyInt(policy.GetProperty("maxReceiveCount"))) == (dlqArn, MaxReceiveCount),
                $"the source queue's RedrivePolicy reads back {policyText}, not this check's DLQ and count");
        }

        var token = Guid.NewGuid().ToString("N")[..8];
        var poison = $"poison-{token}-ünïcode";
        var healthy = $"healthy-{token}";
        var attr = new Dictionary<string, MessageAttributeValue> { ["kind"] = new() { DataType = "String", StringValue = $"order-{token}" } };
        var poisonId = (await sqs.SendMessageAsync(new SendMessageRequest { QueueUrl = srcUrl, MessageBody = poison, MessageAttributes = attr })).MessageId;
        await sqs.SendMessageAsync(new SendMessageRequest { QueueUrl = srcUrl, MessageBody = healthy });

        // Distinct ApproximateReceiveCount values in order of first sight; a duplicate copy that
        // repeats a count does not add one.
        var counts = new Dictionary<string, List<int>> { [poison] = [], [healthy] = [] };
        var healthyDeleted = false;
        var deadline = FidelityTime.StartNew();
        while (deadline.Elapsed.TotalSeconds < 40.0 && !(counts[poison].Contains(MaxReceiveCount) && healthyDeleted))
        {
            foreach (var msg in await Receive(sqs, srcUrl, waitSeconds: 1))
            {
                var body = msg.Body;
                Ensure(counts.ContainsKey(body), $"the source delivered a body this check never sent: {body}");
                var n = int.Parse(msg.Attributes!["ApproximateReceiveCount"]);
                if (!counts[body].Contains(n))
                {
                    counts[body].Add(n);
                }

                if (body == healthy && n >= HealthyDeletedAt)
                {
                    await sqs.DeleteMessageAsync(new DeleteMessageRequest { QueueUrl = srcUrl, ReceiptHandle = msg.ReceiptHandle });
                    healthyDeleted = true;
                    continue;
                }

                if (body == poison && n < MaxReceiveCount)
                {
                    var early = (await Receive(sqs, dlqUrl, waitSeconds: 1)).Where(m => m.Body.Contains(token, StringComparison.Ordinal)).ToList();
                    Ensure(early.Count == 0,
                        $"a message reached the DLQ after a delivery with ApproximateReceiveCount {n} " +
                        $"(maxReceiveCount={MaxReceiveCount})");
                }

                // Re-expose; after the last allowed delivery the next attempt must redrive it.
                await sqs.ChangeMessageVisibilityAsync(new ChangeMessageVisibilityRequest { QueueUrl = srcUrl, ReceiptHandle = msg.ReceiptHandle, VisibilityTimeout = 0 });
            }
        }

        var wantPoison = Enumerable.Range(1, MaxReceiveCount).ToList();
        Ensure(counts[poison].SequenceEqual(wantPoison),
            $"the undeleted message's distinct ApproximateReceiveCount values ran [{string.Join(", ", counts[poison])}], " +
            $"expected [{string.Join(", ", wantPoison)}]");
        var wantHealthy = Enumerable.Range(1, HealthyDeletedAt).ToList();
        Ensure(healthyDeleted && counts[healthy].Take(wantHealthy.Count).SequenceEqual(wantHealthy),
            $"the healthy message's distinct ApproximateReceiveCount values ran [{string.Join(", ", counts[healthy])}], " +
            $"expected to start [{string.Join(", ", wantHealthy)}] before its delete");

        var moved = await AwaitDlqMove(
            sqs,
            srcUrl,
            dlqUrl,
            token: token,
            timeoutSeconds: MoveSeconds,
            what: $"after {MaxReceiveCount} deliveries the undeleted message must move to the DLQ");
        Ensure(moved.Body == poison,
            $"the DLQ copy carries body {moved.Body}, expected {poison}");
        var gotAttr = (moved.MessageAttributes ?? []).GetValueOrDefault("kind");
        Ensure((gotAttr?.DataType, gotAttr?.StringValue) == ("String", $"order-{token}"),
            $"the DLQ copy carries message attribute ({gotAttr?.DataType}, {gotAttr?.StringValue}), expected the one sent");
        await sqs.DeleteMessageAsync(new DeleteMessageRequest { QueueUrl = dlqUrl, ReceiptHandle = moved.ReceiptHandle });

        // The healthy message was deleted below the count: it must never surface in the DLQ. Any
        // further DLQ copy can only be a duplicate of the redriven message, with its MessageId.
        var dlqIds = new HashSet<string> { moved.MessageId };
        List<Message> msgs;
        while ((msgs = await Receive(sqs, dlqUrl, waitSeconds: 2)).Count > 0)
        {
            foreach (var m in msgs)
            {
                Ensure(m.Body == poison,
                    $"the DLQ holds a message besides the redriven one: {m.Body}");
                dlqIds.Add(m.MessageId);
                await sqs.DeleteMessageAsync(new DeleteMessageRequest { QueueUrl = dlqUrl, ReceiptHandle = m.ReceiptHandle });
            }
        }

        Ensure(dlqIds.SetEquals([poisonId]),
            $"the DLQ copies carry {dlqIds.Count} MessageId(s), expected only the redriven message's");
        await AwaitAttribute(
            sqs,
            srcUrl,
            "ApproximateNumberOfMessages",
            "0",
            timeoutSeconds: CounterSeconds,
            why: "a redrive MOVES the message; a source that still counts it was implemented as a copy");
    }

    private static async Task<string> QueueArn(AmazonSQSClient sqs, string url)
        => (await sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest { QueueUrl = url, AttributeNames = ["QueueArn"] })).Attributes!["QueueArn"];

    /// <summary>One ReceiveMessage with the attributes this check reads. Never swallows errors.</summary>
    private static async Task<List<Message>> Receive(AmazonSQSClient sqs, string url, int waitSeconds = 2)
        => (await sqs.ReceiveMessageAsync(new ReceiveMessageRequest
        {
            QueueUrl = url,
            MaxNumberOfMessages = 10,
            WaitTimeSeconds = waitSeconds,
            MessageSystemAttributeNames = ["All"],
            MessageAttributeNames = ["All"],
        })).Messages ?? [];

    /// <summary>
    /// Drive and observe the source -> DLQ move; RAISES on the deadline.
    ///
    /// SQS evaluates the redrive policy when a ReceiveMessage attempts redelivery, so each round
    /// receives from the source before reading the DLQ.
    /// </summary>
    private static async Task<Message> AwaitDlqMove(AmazonSQSClient sqs, string srcUrl, string dlqUrl, string token, double timeoutSeconds, string what)
    {
        var deadline = FidelityTime.StartNew();
        var last = new List<string>();
        while (deadline.Elapsed.TotalSeconds < timeoutSeconds)
        {
            foreach (var msg in await Receive(sqs, srcUrl, waitSeconds: 1))
            {
                await sqs.ChangeMessageVisibilityAsync(new ChangeMessageVisibilityRequest { QueueUrl = srcUrl, ReceiptHandle = msg.ReceiptHandle, VisibilityTimeout = 0 });
            }

            var dlqMsgs = await Receive(sqs, dlqUrl, waitSeconds: 1);
            last = [.. dlqMsgs.Select(m => m.Body)];
            foreach (var msg in dlqMsgs)
            {
                if (msg.Body.Contains(token, StringComparison.Ordinal))
                {
                    return msg;
                }

                await sqs.ChangeMessageVisibilityAsync(new ChangeMessageVisibilityRequest { QueueUrl = dlqUrl, ReceiptHandle = msg.ReceiptHandle, VisibilityTimeout = 0 });
            }
        }

        throw Fail(
            $"{what}: {token} did not reach the DLQ within {timeoutSeconds}s; last DLQ read [{string.Join(", ", last)}]");
    }

    /// <summary>Bounded poll until GetQueueAttributes reports `want` for `name`; RAISES on the deadline.</summary>
    private static async Task<string> AwaitAttribute(AmazonSQSClient sqs, string url, string name, string want, double timeoutSeconds, string why)
    {
        var deadline = FidelityTime.StartNew();
        string? last = null;
        while (deadline.Elapsed.TotalSeconds < timeoutSeconds)
        {
            last = (await sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest { QueueUrl = url, AttributeNames = [name] })).Attributes!.GetValueOrDefault(name);
            if (last == want)
            {
                return last;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(2));
        }

        throw Fail(
            $"{name} on {url} never reached {want} within {timeoutSeconds}s (last={last ?? "None"}); {why}");
    }

    /// <summary>Python's <c>int(x)</c> of a JSON number or numeric string.</summary>
    private static int PyInt(JsonElement value)
        => value.ValueKind == JsonValueKind.Number ? value.GetInt32() : int.Parse(value.GetString()!);

    /// <summary>Python's <c>json.dumps</c> of a flat dict, with its default separators.</summary>
    private static string PyDumps(params (string Key, object Value)[] items)
        => "{" + string.Join(", ", items.Select(kv => $"{JsonSerializer.Serialize(kv.Key)}: {JsonSerializer.Serialize(kv.Value)}")) + "}";
}
