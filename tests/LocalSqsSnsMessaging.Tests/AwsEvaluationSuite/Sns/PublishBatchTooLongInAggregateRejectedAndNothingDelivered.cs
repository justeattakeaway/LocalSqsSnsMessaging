// Translated from behavioral/sns/check_publish_batch_too_long_in_aggregate_rejected_and_nothing_delivered.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Globalization;
using System.Text.Json.Nodes;
using Amazon.SimpleNotificationService.Model;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sns;

/// <summary>sns.publish-batch-too-long-in-aggregate-rejected-and-nothing-delivered</summary>
[Category("AwsEvaluationSuite")]
public sealed class PublishBatchTooLongInAggregateRejectedAndNothingDelivered
{
    private const string Scenario = "sns-batch-aggregate-size";

    private const int EntryBytes = 150 * 1024;

    /// <summary>
    /// PublishBatch refuses two individually legal 150 KiB entries together with BatchRequestTooLong
    /// and delivers neither, while the same entries published as two separate batches both arrive.
    /// </summary>
    [Test]
    [DisplayName("sns.publish-batch-too-long-in-aggregate-rejected-and-nothing-delivered")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sns = ctx.Sns;
        var topicArn = await CreateTopic(ctx);
        var (url, arn) = await CreateQueueForTopic(ctx, topicArn);
        await SubscribeAndConfirm(ctx, topicArn, arn, attributes: new() { ["RawMessageDelivery"] = "true" });

        static (string Head, string Body) Entry(string tag)
        {
            var head = FidelityContext.UniqueName($"sim2real-behav-batchsize-{tag}-");
            return (head, head + new string('x', EntryBytes - head.Length));
        }

        var (_, bodyA) = Entry("refused-a");
        var (_, bodyB) = Entry("refused-b");
        var (code, message) = await Error(() => sns.PublishBatchAsync(new PublishBatchRequest
        {
            TopicArn = topicArn,
            PublishBatchRequestEntries =
            [
                new() { Id = "a", Message = bodyA },
                new() { Id = "b", Message = bodyB },
            ],
        }));
        Ensure(code == "BatchRequestTooLong",
            $"a batch whose entries total 300 KiB gave '{code}', expected 'BatchRequestTooLong'; " +
            $"message '{message}'");
        Ensure(message == "The length of all the messages put together is more than the limit.", $"unexpected refusal message '{message}'");

        // Positive control: each entry alone is a legal batch and is delivered. Its arrival is the
        // barrier for the refused entries, which would have taken the same path.
        var (_, okBodyA) = Entry("ok-a");
        var (_, okBodyB) = Entry("ok-b");
        var started = Monotonic();
        string[] okBodies = [okBodyA, okBodyB];
        for (var i = 0; i < okBodies.Length; i++)
        {
            var resp = await sns.PublishBatchAsync(new PublishBatchRequest
            {
                TopicArn = topicArn,
                PublishBatchRequestEntries = [new() { Id = $"e{i}", Message = okBodies[i] }],
            });
            Ensure(resp.Failed is not { Count: > 0 } && (resp.Successful ?? []).Count == 1,
                $"a single 150 KiB entry must be published, got Failed [{string.Join(", ", (resp.Failed ?? []).Select(f => $"{{'Id': '{f.Id}', 'Code': '{f.Code}', 'Message': '{f.Message}'}}"))}]");
        }

        var seen = await AwaitBodies(ctx, url, [okBodyA, okBodyB], timeoutS: 120.0,
            description: "each entry published in its own batch is delivered",
            forbidden: [bodyA, bodyB]);
        var latency = Math.Max(seen.Values.Max() - started, 1.0);
        var window = Math.Max(3.0 * latency, 20.0);
        var late = (await Drain(ctx, new() { ["queue"] = url }, windowS: window))["queue"];
        Ensure(late.Count == 0,
            $"no entry of the refused batch may be delivered; within {window.ToString("F0", CultureInfo.InvariantCulture)}s the queue " +
            $"received bodies starting {Repr(late.Select(b => Head(b, 60)))}");
    }

    /// <summary>time.monotonic(), on the check's virtual clock.</summary>
    private static double Monotonic() => FidelityTime.UtcNow.ToUnixTimeMilliseconds() / 1000.0;

    private static string Head(string s, int n) => s.Length <= n ? s : s[..n];

    private static string Repr(IEnumerable<string> items) => "[" + string.Join(", ", items.Select(s => $"'{s}'")) + "]";

    private static List<Amazon.SimpleNotificationService.Model.Tag> TagsKv() =>
        [new() { Key = "sim2real", Value = Scenario }, new() { Key = "sim2real:managed", Value = "true" }];

    /// <summary>CreateTopic (tagged + prefix-named) and register DeleteTopic immediately.</summary>
    private static async Task<string> CreateTopic(FidelityContext ctx, bool fifo = false, Dictionary<string, string>? attributes = null)
    {
        var sns = ctx.Sns;
        var name = FidelityContext.UniqueName("sim2real-behav-sns-t-") + (fifo ? ".fifo" : "");
        var attrs = new Dictionary<string, string>(attributes ?? []);
        if (fifo)
        {
            attrs["FifoTopic"] = "true";
        }

        var arn = (await sns.CreateTopicAsync(new CreateTopicRequest { Name = name, Attributes = attrs, Tags = TagsKv() })).TopicArn;
        ctx.OnCleanup(() => ctx.Sns.DeleteTopicAsync(new DeleteTopicRequest { TopicArn = arn }));
        return arn;
    }

    /// <summary>Create a real SQS queue whose policy lets exactly this topic send to it.</summary>
    private static async Task<(string Url, string Arn)> CreateQueueForTopic(FidelityContext ctx, string topicArn, bool fifo = false,
        Dictionary<string, string>? attributes = null)
    {
        var sqs = ctx.Sqs;
        var name = FidelityContext.UniqueName("sim2real-behav-sns-q-") + (fifo ? ".fifo" : "");
        var attrs = new Dictionary<string, string>(attributes ?? []);
        if (fifo)
        {
            attrs["FifoQueue"] = "true";
        }

        var url = (await sqs.CreateQueueAsync(new CreateQueueRequest
        {
            QueueName = name,
            Attributes = attrs,
            Tags = new() { ["sim2real"] = Scenario, ["sim2real:managed"] = "true" },
        })).QueueUrl;
        ctx.OnCleanup(() => ctx.Sqs.DeleteQueueAsync(url));
        var arn = (await sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest { QueueUrl = url, AttributeNames = ["QueueArn"] })).Attributes["QueueArn"];
        var policy = new JsonObject
        {
            ["Version"] = "2012-10-17",
            ["Statement"] = new JsonArray(new JsonObject
            {
                ["Effect"] = "Allow",
                ["Principal"] = new JsonObject { ["Service"] = "sns.amazonaws.com" },
                ["Action"] = "sqs:SendMessage",
                ["Resource"] = arn,
                ["Condition"] = new JsonObject { ["ArnEquals"] = new JsonObject { ["aws:SourceArn"] = topicArn } },
            }),
        };
        await sqs.SetQueueAttributesAsync(new SetQueueAttributesRequest { QueueUrl = url, Attributes = new() { ["Policy"] = policy.ToJsonString() } });
        return (url, arn);
    }

    /// <summary>Subscribe an SQS queue and poll GetSubscriptionAttributes until it is confirmed.</summary>
    private static async Task<string> SubscribeAndConfirm(FidelityContext ctx, string topicArn, string queueArn,
        Dictionary<string, string>? attributes = null, double timeoutS = 60.0)
    {
        var sns = ctx.Sns;
        var request = new SubscribeRequest { TopicArn = topicArn, Protocol = "sqs", Endpoint = queueArn, ReturnSubscriptionArn = true };
        if (attributes is { Count: > 0 })
        {
            request.Attributes = attributes;
        }

        var subArn = (await sns.SubscribeAsync(request)).SubscriptionArn;
        ctx.OnCleanup(() => ctx.Sns.UnsubscribeAsync(new UnsubscribeRequest { SubscriptionArn = subArn }));
        await PollUntil(
            async () => (await sns.GetSubscriptionAttributesAsync(new GetSubscriptionAttributesRequest { SubscriptionArn = subArn }))
                .Attributes?.GetValueOrDefault("PendingConfirmation"),
            pending => pending == "false",
            timeoutS: timeoutS, intervalS: 2.0,
            description: "the new subscription leaves PendingConfirmation");
        return subArn;
    }

    /// <summary>Bounded retry; RAISES on the deadline so a timeout can never read as a pass.</summary>
    private static async Task<T> PollUntil<T>(Func<Task<T>> fn, Func<T, bool> pred, double timeoutS, double intervalS, string description)
    {
        var deadline = FidelityTime.StartNew();
        var value = await fn();
        while (!pred(value))
        {
            if (deadline.Elapsed >= TimeSpan.FromSeconds(timeoutS))
            {
                throw Fail($"{description}: not satisfied within {timeoutS}s, last value '{value}'");
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(intervalS));
            value = await fn();
        }

        return value;
    }

    /// <summary>
    /// Long-poll until every wanted body is seen; RAISES on the deadline.
    /// Returns {body: monotonic time first seen}. A body in `forbidden` that arrives raises at
    /// once: it is a leak, and swallowing it would hide what the caller is about to assert.
    /// </summary>
    private static async Task<Dictionary<string, double>> AwaitBodies(FidelityContext ctx, string queueUrl, HashSet<string> wanted,
        double timeoutS, string description, HashSet<string>? forbidden = null)
    {
        var sqs = ctx.Sqs;
        var seen = new Dictionary<string, double>();
        var outstanding = new HashSet<string>(wanted);
        var deadline = FidelityTime.StartNew();
        while (outstanding.Count > 0)
        {
            var resp = await sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = queueUrl, MaxNumberOfMessages = 10, WaitTimeSeconds = 5 });
            foreach (var msg in resp.Messages ?? [])
            {
                await sqs.DeleteMessageAsync(new DeleteMessageRequest { QueueUrl = queueUrl, ReceiptHandle = msg.ReceiptHandle });
                var body = msg.Body;
                if (forbidden is { Count: > 0 } && forbidden.Contains(body))
                {
                    throw Fail($"{description}: a body that must never be delivered arrived: '{body}'");
                }

                if (outstanding.Remove(body))
                {
                    seen[body] = Monotonic();
                }
            }

            if (outstanding.Count > 0 && deadline.Elapsed >= TimeSpan.FromSeconds(timeoutS))
            {
                throw Fail($"{description}: {Repr(outstanding.Order(StringComparer.Ordinal))} never arrived within {timeoutS}s");
            }
        }

        return seen;
    }

    /// <summary>
    /// Alternate long polls across the queues for one shared window and return every body
    /// each produced. The empty lists feed an equality-to-empty assertion.
    /// </summary>
    private static async Task<Dictionary<string, List<string>>> Drain(FidelityContext ctx, Dictionary<string, string> queues, double windowS)
    {
        var sqs = ctx.Sqs;
        var output = queues.Keys.ToDictionary(label => label, _ => new List<string>());
        var deadline = FidelityTime.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(windowS))
        {
            foreach (var (label, queueUrl) in queues)
            {
                var resp = await sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = queueUrl, MaxNumberOfMessages = 10, WaitTimeSeconds = 2 });
                foreach (var msg in resp.Messages ?? [])
                {
                    await sqs.DeleteMessageAsync(new DeleteMessageRequest { QueueUrl = queueUrl, ReceiptHandle = msg.ReceiptHandle });
                    output[label].Add(msg.Body);
                }
            }
        }

        return output;
    }

    /// <summary>
    /// (Error.Code, Error.Message) of the ClientError fn raises. An unexpected success
    /// raises too, naming what came back, so it can never read as a code mismatch.
    /// </summary>
    private static async Task<(string Code, string Message)> Error<T>(Func<Task<T>> fn)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is null)
        {
            throw Fail($"expected the call to be refused, but it succeeded: {result}");
        }

        return (error.Code ?? "", error.Message ?? "");
    }
}
