// Translated from behavioral/sqs/check_redriven_message_restarts_its_receive_count_and_dead_letters_again_at_maxreceivecount.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Text.Json.Nodes;
using Amazon.SQS;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sqs;

/// <summary>sqs.redriven-message-restarts-its-receive-count-and-dead-letters-again-at-maxreceivecount</summary>
[Category("AwsEvaluationSuite")]
public sealed class RedrivenMessageRestartsItsReceiveCountAndDeadLettersAgainAtMaxreceivecount
{
    private const int MaxReceiveCount = 2;
    private const double MoveS = 40.0; // bounded wait for each asynchronous source -> DLQ move
    private const double TaskS = 40.0; // bounded wait for the message move task to settle
    private const double CounterS = 40.0; // bounded wait for the approximate counters to converge

    private static Dictionary<string, string> Tags() => new() { ["sim2real:managed"] = "true", ["sim2real"] = "redriven-receive-count" };

    /// <summary>
    /// A message dead-lettered at maxReceiveCount=2 and moved back with StartMessageMoveTask starts
    /// over at ApproximateReceiveCount 1 on the source, is withheld from the DLQ for two more
    /// deliveries, and then dead-letters again with the same body.
    ///
    /// Reference: https://docs.aws.amazon.com/AWSSimpleQueueService/latest/APIReference/API_StartMessageMoveTask.html
    /// </summary>
    [Test]
    [DisplayName("sqs.redriven-message-restarts-its-receive-count-and-dead-letters-again-at-maxreceivecount")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sqs = ctx.Sqs;

        var dlqUrl = (await sqs.CreateQueueAsync(new CreateQueueRequest { QueueName = FidelityContext.UniqueName("sim2real-behav-dlq-"), Tags = Tags() })).QueueUrl;
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(dlqUrl));
        var dlqArn = await QueueArn(sqs, dlqUrl);

        var srcUrl = (await sqs.CreateQueueAsync(new CreateQueueRequest
        {
            QueueName = FidelityContext.UniqueName("sim2real-behav-src-"),
            Attributes = new()
            {
                ["VisibilityTimeout"] = "30",
                ["RedrivePolicy"] = $"{{\"deadLetterTargetArn\": {JsonValue.Create(dlqArn).ToJsonString()}, \"maxReceiveCount\": {MaxReceiveCount}}}",
            },
            Tags = Tags(),
        })).QueueUrl;
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(srcUrl));

        var token = Guid.NewGuid().ToString("N")[..8];
        var body = $"retry-me-{token}";
        await sqs.SendMessageAsync(new SendMessageRequest { QueueUrl = srcUrl, MessageBody = body });

        await DeliverUpToTheCount(sqs, srcUrl, dlqUrl, token: token, body: body, roundName: "first round");
        var first = await AwaitDlqMove(sqs, srcUrl, dlqUrl, token: token, timeoutS: MoveS,
            what: "after two deliveries the message must move to the DLQ");
        Ensure(first.Body == body, $"the first DLQ copy carries '{first.Body}', expected '{body}'");
        // Re-expose the DLQ copy so the move task can take it.
        await sqs.ChangeMessageVisibilityAsync(new ChangeMessageVisibilityRequest
        {
            QueueUrl = dlqUrl,
            ReceiptHandle = first.ReceiptHandle,
            VisibilityTimeout = 0,
        });
        // The move task sizes itself from the DLQ's counters, so they must show the message visible.
        await AwaitCounts(sqs, dlqUrl, minVisible: 1, inFlight: "0", timeoutS: CounterS);

        var handle = (await sqs.StartMessageMoveTaskAsync(new StartMessageMoveTaskRequest { SourceArn = dlqArn })).TaskHandle;
        Ensure(handle is not null && handle.Trim().Length > 0,
            $"StartMessageMoveTask returned TaskHandle '{handle}', expected a non-empty string");
        var task = await AwaitTask(sqs, dlqArn, 1, timeoutS: TaskS);
        // The moved-message counter is approximate; the move itself is graded by the source
        // delivering the message again below.
        var got = (task.Status, task.SourceArn);
        Ensure(got == ("COMPLETED", dlqArn),
            $"the move task settled as (Status, SourceArn) {got}, expected ('COMPLETED', <dlq arn>)");
        // A task started without DestinationArn reports none; it moves back to the original source.
        Ensure(task.DestinationArn is null,
            $"a task started without DestinationArn lists DestinationArn '{task.DestinationArn}'");

        // Back on the source the message is a fresh message: the count restarts and the policy applies anew.
        await DeliverUpToTheCount(sqs, srcUrl, dlqUrl, token: token, body: body, roundName: "after redrive");
        var second = await AwaitDlqMove(sqs, srcUrl, dlqUrl, token: token, timeoutS: MoveS,
            what: "after two more deliveries the redriven message must dead-letter again");
        Ensure(second.Body == body, $"the second DLQ copy carries '{second.Body}', expected '{body}'");
        await sqs.DeleteMessageAsync(new DeleteMessageRequest { QueueUrl = dlqUrl, ReceiptHandle = second.ReceiptHandle });
        var left = (await Receive(sqs, srcUrl, waitS: 2)).Where(m => m.Body.Contains(token, StringComparison.Ordinal)).Select(m => m.Body).ToList();
        Ensure(left.Count == 0, $"the source still delivers the dead-lettered message [{string.Join(", ", left)}]");
    }

    private static async Task<string> QueueArn(IAmazonSQS sqs, string url) =>
        (await sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest { QueueUrl = url, AttributeNames = ["QueueArn"] })).Attributes!["QueueArn"];

    /// <summary>One ReceiveMessage with the system attributes this check reads. Never swallows errors.</summary>
    private static async Task<List<Message>> Receive(IAmazonSQS sqs, string url, int waitS = 2) =>
        (await sqs.ReceiveMessageAsync(new ReceiveMessageRequest
        {
            QueueUrl = url,
            MaxNumberOfMessages = 10,
            WaitTimeSeconds = waitS,
            MessageSystemAttributeNames = ["All"],
        })).Messages ?? [];

    /// <summary>Bounded poll until the tokened message is delivered from <paramref name="url"/>; RAISES on the deadline.</summary>
    private static async Task<Message> AwaitDelivery(IAmazonSQS sqs, string url, string token, double timeoutS, string what)
    {
        var deadline = FidelityTime.StartNew();
        List<(string Body, string? Count)> last = [];
        while (deadline.Elapsed < TimeSpan.FromSeconds(timeoutS))
        {
            var msgs = await Receive(sqs, url);
            last = msgs.Select(m => (m.Body, m.Attributes!.GetValueOrDefault("ApproximateReceiveCount"))).ToList();
            foreach (var msg in msgs)
            {
                if (msg.Body.Contains(token, StringComparison.Ordinal))
                {
                    return msg;
                }

                await sqs.ChangeMessageVisibilityAsync(new ChangeMessageVisibilityRequest
                {
                    QueueUrl = url,
                    ReceiptHandle = msg.ReceiptHandle,
                    VisibilityTimeout = 0,
                });
            }
        }

        throw Fail($"{what}: '{token}' was not delivered from {url} within {timeoutS}s; last read [{string.Join(", ", last)}]");
    }

    /// <summary>Drive and observe the source -> DLQ move; RAISES on the deadline.</summary>
    private static async Task<Message> AwaitDlqMove(IAmazonSQS sqs, string srcUrl, string dlqUrl, string token, double timeoutS, string what)
    {
        var deadline = FidelityTime.StartNew();
        List<string> last = [];
        while (deadline.Elapsed < TimeSpan.FromSeconds(timeoutS))
        {
            foreach (var msg in await Receive(sqs, srcUrl, waitS: 1))
            {
                await sqs.ChangeMessageVisibilityAsync(new ChangeMessageVisibilityRequest
                {
                    QueueUrl = srcUrl,
                    ReceiptHandle = msg.ReceiptHandle,
                    VisibilityTimeout = 0,
                });
            }

            var dlqMsgs = await Receive(sqs, dlqUrl, waitS: 1);
            last = dlqMsgs.Select(m => m.Body).ToList();
            foreach (var msg in dlqMsgs)
            {
                if (msg.Body.Contains(token, StringComparison.Ordinal))
                {
                    return msg;
                }

                await sqs.ChangeMessageVisibilityAsync(new ChangeMessageVisibilityRequest
                {
                    QueueUrl = dlqUrl,
                    ReceiptHandle = msg.ReceiptHandle,
                    VisibilityTimeout = 0,
                });
            }
        }

        throw Fail($"{what}: '{token}' did not reach the DLQ within {timeoutS}s; last DLQ read [{string.Join(", ", last)}]");
    }

    /// <summary>
    /// Receive the message from the source until ApproximateReceiveCount reaches maxReceiveCount;
    /// the distinct counts, in order of first sight, must run 1..maxReceiveCount while the DLQ holds
    /// nothing of ours. A duplicate copy that repeats a count adds none.
    /// </summary>
    private static async Task DeliverUpToTheCount(IAmazonSQS sqs, string srcUrl, string dlqUrl, string token, string body, string roundName)
    {
        List<int> seen = [];
        while (!seen.Contains(MaxReceiveCount))
        {
            var msg = await AwaitDelivery(sqs, srcUrl, token: token, timeoutS: 30.0,
                what: $"{roundName}: delivery after counts [{string.Join(", ", seen)}]");
            Ensure(msg.Body == body, $"{roundName}: a delivery carried '{msg.Body}', expected '{body}'");
            var n = int.Parse(msg.Attributes!["ApproximateReceiveCount"]);
            if (!seen.Contains(n))
            {
                seen.Add(n);
            }

            Ensure(seen.SequenceEqual(Enumerable.Range(1, seen.Count)),
                $"{roundName}: distinct ApproximateReceiveCount values ran [{string.Join(", ", seen)}], expected 1, 2, ...");
            var early = (await Receive(sqs, dlqUrl, waitS: 1)).Where(m => m.Body.Contains(token, StringComparison.Ordinal)).ToList();
            Ensure(early.Count == 0, $"{roundName}: the message reached the DLQ after a count-{n} delivery");
            await sqs.ChangeMessageVisibilityAsync(new ChangeMessageVisibilityRequest
            {
                QueueUrl = srcUrl,
                ReceiptHandle = msg.ReceiptHandle,
                VisibilityTimeout = 0,
            });
        }
    }

    /// <summary>
    /// Bounded poll until at least <paramref name="minVisible"/> messages are visible and <paramref name="inFlight"/> are in
    /// flight; RAISES on the deadline.
    /// </summary>
    private static async Task AwaitCounts(IAmazonSQS sqs, string url, int minVisible, string inFlight, double timeoutS)
    {
        List<string> names = ["ApproximateNumberOfMessages", "ApproximateNumberOfMessagesNotVisible"];
        var deadline = FidelityTime.StartNew();
        (string? Visible, string? InFlight)? last = null;
        while (deadline.Elapsed < TimeSpan.FromSeconds(timeoutS))
        {
            var attrs = (await sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest { QueueUrl = url, AttributeNames = names })).Attributes!;
            last = (attrs.GetValueOrDefault(names[0]), attrs.GetValueOrDefault(names[1]));
            if (int.Parse(string.IsNullOrEmpty(last.Value.Visible) ? "0" : last.Value.Visible) >= minVisible && last.Value.InFlight == inFlight)
            {
                return;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(2));
        }

        throw Fail($"counters on {url} read {last} after {timeoutS}s, expected at least {minVisible} "
                   + $"visible and '{inFlight}' in flight");
    }

    /// <summary>
    /// Bounded poll of ListMessageMoveTasks until the <paramref name="started"/>-th task (newest first) is listed
    /// and has left RUNNING; RAISES on the deadline.
    /// </summary>
    private static async Task<ListMessageMoveTasksResultEntry> AwaitTask(IAmazonSQS sqs, string dlqArn, int started, double timeoutS)
    {
        var deadline = FidelityTime.StartNew();
        ListMessageMoveTasksResultEntry? last = null;
        while (deadline.Elapsed < TimeSpan.FromSeconds(timeoutS))
        {
            var results = (await sqs.ListMessageMoveTasksAsync(new ListMessageMoveTasksRequest { SourceArn = dlqArn, MaxResults = 10 })).Results ?? [];
            last = results.Count >= started ? results[0] : null;
            if (last is not null && last.Status != "RUNNING")
            {
                return last;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(3));
        }

        throw Fail($"the message move task did not settle within {timeoutS}s; last saw "
                   + (last is null ? "None" : $"{{TaskHandle={last.TaskHandle}, Status={last.Status}, SourceArn={last.SourceArn}}}"));
    }
}
