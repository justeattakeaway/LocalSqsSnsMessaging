// Translated from behavioral/sqs/check_dlq_move_withheld_until_the_maxreceivecount_th_delivery.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Diagnostics;
using System.Text.Json;
using Amazon.SQS;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sqs;

/// <summary>sqs.dlq-move-withheld-until-the-maxreceivecount-th-delivery</summary>
[Category("AwsEvaluationSuite")]
public sealed class DlqMoveWithheldUntilTheMaxreceivecountThDelivery
{
    // 3 so that moving on an early receive is distinguishable from moving on time.
    private const int MaxReceiveCount = 3;
    private const double MoveSeconds = 90.0; // bounded wait for the asynchronous source -> DLQ move
    private const double CounterSeconds = 120.0; // bounded wait for the documented >=1min counter consistency window

    private static readonly Dictionary<string, string> Tags = new()
    {
        ["sim2real:managed"] = "true",
        ["sim2real"] = "dlq-move-withheld",
    };

    /// <summary>
    /// With maxReceiveCount=3 the source delivers ApproximateReceiveCount 1, 2, 3 while the DLQ
    /// stays empty; only then the byte-identical body is in the DLQ and the source drains to 0.
    ///
    /// Reference: https://docs.aws.amazon.com/AWSSimpleQueueService/latest/SQSDeveloperGuide/sqs-dead-letter-queues.html
    /// Reference: https://docs.aws.amazon.com/AWSSimpleQueueService/latest/APIReference/API_GetQueueAttributes.html
    /// </summary>
    [Test]
    [DisplayName("sqs.dlq-move-withheld-until-the-maxreceivecount-th-delivery")]
    [Timeout(FidelityContext.BudgetMilliseconds)]
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
                // 30s is only a ceiling: every redelivery below is forced with
                // ChangeMessageVisibility(0), so the check never waits out a visibility timeout.
                ["VisibilityTimeout"] = "30",
                ["RedrivePolicy"] = PyDumps(("deadLetterTargetArn", dlqArn), ("maxReceiveCount", MaxReceiveCount)),
            },
            Tags = new(Tags),
        })).QueueUrl;
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(srcUrl));

        // The policy must actually be on the queue, or every verdict below is about a queue with no
        // redrive policy at all.
        using var policyDoc = JsonDocument.Parse(
            (await sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest { QueueUrl = srcUrl, AttributeNames = ["RedrivePolicy"] })).Attributes!["RedrivePolicy"]);
        var policy = policyDoc.RootElement;
        Ensure(PyInt(policy.GetProperty("maxReceiveCount")) == MaxReceiveCount,
            $"the source queue's maxReceiveCount is {Get(policy, "maxReceiveCount")}, not " +
            $"{MaxReceiveCount}; the delivery-count assertions below would be measuring the wrong " +
            "policy");
        Ensure(policy.GetProperty("deadLetterTargetArn").GetString() == dlqArn,
            $"the source queue's deadLetterTargetArn is {Get(policy, "deadLetterTargetArn")}, not " +
            $"the DLQ this check created ({dlqArn})");

        var token = Guid.NewGuid().ToString("N")[..8];
        var body = $"poison-{token}";
        await sqs.SendMessageAsync(new SendMessageRequest { QueueUrl = srcUrl, MessageBody = body });

        // --- deliveries 1..N must all come from the SOURCE, correctly counted ---------------
        for (var expectedCount = 1; expectedCount < MaxReceiveCount + 1; expectedCount++)
        {
            var msg = await AwaitDelivery(
                sqs,
                srcUrl,
                token: token,
                timeoutSeconds: 30.0,
                what: $"delivery {expectedCount} of {MaxReceiveCount}");
            Ensure(msg.Body == body,
                $"delivery {expectedCount} from the source carried body {msg.Body}, expected " +
                $"{body}");
            var gotCount = msg.Attributes!["ApproximateReceiveCount"];
            Ensure(gotCount == expectedCount.ToString(),
                $"delivery {expectedCount} of {MaxReceiveCount} reported " +
                $"ApproximateReceiveCount={gotCount}; the counter must advance 1,2,...," +
                $"{MaxReceiveCount} across redeliveries from the source");
            // Corroborating second observable: with ReceiveCount == expected_count <=
            // maxReceiveCount, the move is not yet due, so the DLQ must hold nothing of ours.
            var early = (await Receive(sqs, dlqUrl, waitSeconds: 1)).Where(m => m.Body.Contains(token, StringComparison.Ordinal)).ToList();
            Ensure(early.Count == 0,
                $"the message reached the DLQ after only {expectedCount} source " +
                $"deliver{(expectedCount == 1 ? "y" : "ies")} (maxReceiveCount=" +
                $"{MaxReceiveCount}): the redrive policy was evaluated too early");
            // Force the next redelivery instead of waiting out the visibility timeout.
            await sqs.ChangeMessageVisibilityAsync(new ChangeMessageVisibilityRequest { QueueUrl = srcUrl, ReceiptHandle = msg.ReceiptHandle, VisibilityTimeout = 0 });
        }

        // --- the move: an independent read at the DESTINATION ------------------------------
        var moved = await AwaitDlqMove(
            sqs,
            srcUrl,
            dlqUrl,
            token: token,
            timeoutSeconds: MoveSeconds,
            what: $"after {MaxReceiveCount} source deliveries the message must move to the DLQ, " +
                  "but nothing arrived there");
        Ensure(moved.Body == body,
            $"the DLQ copy is not byte-identical to what was sent: got {moved.Body}, expected " +
            $"{body}");
        // Deliberately not asserted: whether the DLQ copy's ApproximateReceiveCount restarts at 1 or
        // continues from the source's count; the docs do not say.
        await sqs.DeleteMessageAsync(new DeleteMessageRequest { QueueUrl = dlqUrl, ReceiptHandle = moved.ReceiptHandle });

        // --- and it is GONE from the source, not copied ------------------------------------
        await AwaitAttribute(
            sqs,
            srcUrl,
            "ApproximateNumberOfMessages",
            "0",
            timeoutSeconds: CounterSeconds,
            why: "a redrive MOVES the message; a source that still counts it was implemented as a copy");
        var left = (await Receive(sqs, srcUrl, waitSeconds: 2)).Where(m => m.Body.Contains(token, StringComparison.Ordinal)).Select(m => m.Body).ToList();
        Ensure(left.Count == 0,
            $"the source still delivers the redriven message [{string.Join(", ", left)}]; the move must remove it from " +
            "the source, not duplicate it into the DLQ");
    }

    private static async Task<string> QueueArn(AmazonSQSClient sqs, string url)
        => (await sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest { QueueUrl = url, AttributeNames = ["QueueArn"] })).Attributes!["QueueArn"];

    /// <summary>One ReceiveMessage with the system attributes this check reads. Never swallows errors.</summary>
    private static async Task<List<Message>> Receive(AmazonSQSClient sqs, string url, int waitSeconds = 2)
        => (await sqs.ReceiveMessageAsync(new ReceiveMessageRequest
        {
            QueueUrl = url,
            MaxNumberOfMessages = 10,
            WaitTimeSeconds = waitSeconds,
            MessageSystemAttributeNames = ["All"],
        })).Messages ?? [];

    /// <summary>
    /// Bounded poll until the tokened message is delivered from `url`; RAISES on the deadline.
    ///
    /// Non-matching messages are re-exposed with ChangeMessageVisibility(0); nothing is deleted.
    /// </summary>
    private static async Task<Message> AwaitDelivery(AmazonSQSClient sqs, string url, string token, double timeoutSeconds, string what)
    {
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed.TotalSeconds < timeoutSeconds)
        {
            foreach (var msg in await Receive(sqs, url))
            {
                if (msg.Body.Contains(token, StringComparison.Ordinal))
                {
                    return msg;
                }

                await sqs.ChangeMessageVisibilityAsync(new ChangeMessageVisibilityRequest { QueueUrl = url, ReceiptHandle = msg.ReceiptHandle, VisibilityTimeout = 0 });
            }
        }

        throw Fail($"{what}: {token} was not delivered from {url} within {timeoutSeconds}s");
    }

    /// <summary>
    /// Drive and observe the source -> DLQ move; RAISES on the deadline.
    ///
    /// SQS evaluates the redrive policy when a ReceiveMessage attempts redelivery, so each round
    /// receives from the source before reading the DLQ.
    /// </summary>
    private static async Task<Message> AwaitDlqMove(AmazonSQSClient sqs, string srcUrl, string dlqUrl, string token, double timeoutSeconds, string what)
    {
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed.TotalSeconds < timeoutSeconds)
        {
            foreach (var msg in await Receive(sqs, srcUrl, waitSeconds: 1))
            {
                await sqs.ChangeMessageVisibilityAsync(new ChangeMessageVisibilityRequest { QueueUrl = srcUrl, ReceiptHandle = msg.ReceiptHandle, VisibilityTimeout = 0 });
            }

            foreach (var msg in await Receive(sqs, dlqUrl, waitSeconds: 1))
            {
                if (msg.Body.Contains(token, StringComparison.Ordinal))
                {
                    return msg;
                }

                await sqs.ChangeMessageVisibilityAsync(new ChangeMessageVisibilityRequest { QueueUrl = dlqUrl, ReceiptHandle = msg.ReceiptHandle, VisibilityTimeout = 0 });
            }
        }

        throw Fail($"{what}: {token} did not reach the DLQ within {timeoutSeconds}s");
    }

    /// <summary>Bounded poll until GetQueueAttributes reports `want` for `name`; RAISES on the deadline.</summary>
    private static async Task<string> AwaitAttribute(AmazonSQSClient sqs, string url, string name, string want, double timeoutSeconds, string why)
    {
        var deadline = Stopwatch.StartNew();
        string? last = null;
        while (deadline.Elapsed.TotalSeconds < timeoutSeconds)
        {
            last = (await sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest { QueueUrl = url, AttributeNames = [name] })).Attributes!.GetValueOrDefault(name);
            if (last == want)
            {
                return last;
            }

            await Task.Delay(TimeSpan.FromSeconds(2));
        }

        throw Fail(
            $"{name} on {url} never reached {want} within {timeoutSeconds}s (last={last ?? "None"}); {why}");
    }

    /// <summary>Python's <c>int(x)</c> of a JSON number or numeric string.</summary>
    private static int PyInt(JsonElement value)
        => value.ValueKind == JsonValueKind.Number ? value.GetInt32() : int.Parse(value.GetString()!);

    private static string Get(JsonElement obj, string name)
        => obj.TryGetProperty(name, out var value) ? value.GetRawText() : "None";

    /// <summary>Python's <c>json.dumps</c> of a flat dict, with its default separators.</summary>
    private static string PyDumps(params (string Key, object Value)[] items)
        => "{" + string.Join(", ", items.Select(kv => $"{JsonSerializer.Serialize(kv.Key)}: {JsonSerializer.Serialize(kv.Value)}")) + "}";
}
