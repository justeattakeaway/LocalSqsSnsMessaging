// Translated from behavioral/sns/check_publish_batch_filter_policy_is_evaluated_per_entry.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Globalization;
using System.Text.Json.Nodes;
using Amazon.SimpleNotificationService.Model;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;
using SnsMessageAttributeValue = Amazon.SimpleNotificationService.Model.MessageAttributeValue;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sns;

/// <summary>sns.publish-batch-filter-policy-is-evaluated-per-entry</summary>
[Category("AwsEvaluationSuite")]
public sealed class PublishBatchFilterPolicyIsEvaluatedPerEntry
{
    private const string Scenario = "sns-batch-filter";

    /// <summary>
    /// A FilterPolicy {"kind": ["keep"]} is evaluated per PublishBatch entry: of four entries
    /// (keep, drop, keep, no attribute) the filtered subscription receives exactly the two "keep"
    /// entries while the unfiltered twin receives all four.
    /// </summary>
    [Test]
    [DisplayName("sns.publish-batch-filter-policy-is-evaluated-per-entry")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sns = ctx.Sns;
        var mono = FidelityTime.StartNew();
        var topicArn = await CreateTopic(ctx);

        var (controlUrl, controlArn) = await CreateQueueForTopic(ctx, topicArn);
        await SubscribeAndConfirm(ctx, topicArn, controlArn, attributes: new() { ["RawMessageDelivery"] = "true" });
        var (filteredUrl, filteredArn) = await CreateQueueForTopic(ctx, topicArn);
        await SubscribeAndConfirm(ctx, topicArn, filteredArn, attributes: new()
        {
            ["RawMessageDelivery"] = "true",
            ["FilterPolicy"] = new JsonObject { ["kind"] = new JsonArray("keep") }.ToJsonString(),
        });

        var marks = Enumerable.Range(1, 4).Select(i => FidelityContext.UniqueName($"sim2real-behav-batchflt-e{i}-")).ToList();
        string?[] kinds = ["keep", "drop", "keep", null];
        var entries = new List<PublishBatchRequestEntry>();
        for (var i = 1; i <= 4; i++)
        {
            var (marker, kind) = (marks[i - 1], kinds[i - 1]);
            var entry = new PublishBatchRequestEntry { Id = $"e{i}", Message = marker };
            if (!string.IsNullOrEmpty(kind))
            {
                entry.MessageAttributes = new() { ["kind"] = S(kind) };
            }

            entries.Add(entry);
        }

        var started = mono.Elapsed.TotalSeconds;
        var resp = await sns.PublishBatchAsync(new PublishBatchRequest { TopicArn = topicArn, PublishBatchRequestEntries = entries });
        var ok = (resp.Successful ?? []).Select(e => e.Id).Order(StringComparer.Ordinal).ToList();
        var failed = resp.Failed ?? [];
        Ensure(ok.SequenceEqual(["e1", "e2", "e3", "e4"]) && failed.Count == 0,
            "PublishBatch must report all four entries Successful and none Failed; Successful " +
            $"ids {List(ok)}, Failed [{string.Join(", ", failed.Select(f => $"{{'Id': '{f.Id}', 'Code': '{f.Code}', 'Message': '{f.Message}'}}"))}]");

        await GradeMatrix(ctx, mono, controlUrl, new() { ["filtered"] = (filteredUrl, [marks[0], marks[2]]) },
            [.. marks], started);
    }

    // Helpers: an SQS queue whose policy admits exactly this topic, a confirmed subscription, and
    // long-poll waits that raise on their deadline so a timeout can never read as a pass.

    /// <summary>A String-typed message attribute value.</summary>
    private static SnsMessageAttributeValue S(string value) => new() { DataType = "String", StringValue = value };

    private static string List(IEnumerable<string> items) => "[" + string.Join(", ", items.Select(s => $"'{s}'")) + "]";

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
            QueueName = name, Attributes = attrs,
            Tags = new() { ["sim2real"] = Scenario, ["sim2real:managed"] = "true" },
        })).QueueUrl;
        ctx.OnCleanup(() => ctx.Sqs.DeleteQueueAsync(url));
        var arn = (await sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest { QueueUrl = url, AttributeNames = ["QueueArn"] })).Attributes!["QueueArn"];
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
    /// </summary>
    /// <remarks>
    /// Returns {body: monotonic time first seen}. A body in <c>forbidden</c> that arrives raises at
    /// once: it is a leak, and swallowing it would hide what the caller is about to assert.
    /// </remarks>
    private static async Task<Dictionary<string, double>> AwaitBodies(FidelityContext ctx, FidelityStopwatch mono, string queueUrl,
        HashSet<string> wanted, double timeoutS, string description, HashSet<string>? forbidden = null)
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
                    seen[body] = mono.Elapsed.TotalSeconds;
                }
            }

            if (outstanding.Count > 0 && deadline.Elapsed >= TimeSpan.FromSeconds(timeoutS))
            {
                throw Fail($"{description}: {List(outstanding.Order(StringComparer.Ordinal))} never arrived within {timeoutS}s");
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
            foreach (var (label, url) in queues)
            {
                var resp = await sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = url, MaxNumberOfMessages = 10, WaitTimeSeconds = 2 });
                foreach (var msg in resp.Messages ?? [])
                {
                    await sqs.DeleteMessageAsync(new DeleteMessageRequest { QueueUrl = url, ReceiptHandle = msg.ReceiptHandle });
                    output[label].Add(msg.Body);
                }
            }
        }

        return output;
    }

    /// <summary>
    /// Grade every filtered arm against one barrier.
    /// </summary>
    /// <remarks>
    /// The unfiltered control receives every published body first; that arrival is the barrier.
    /// Each arm must then receive exactly its expected set (a body outside it raises at once),
    /// and a shared window calibrated from the measured control latency must yield nothing more
    /// on any arm, read twice: by draining, and by ApproximateNumberOfMessages.
    /// </remarks>
    private static async Task GradeMatrix(FidelityContext ctx, FidelityStopwatch mono, string controlUrl,
        Dictionary<string, (string Url, HashSet<string> Expected)> arms, HashSet<string> published, double started)
    {
        var sqs = ctx.Sqs;
        var controlSeen = await AwaitBodies(ctx, mono, controlUrl, published, timeoutS: 120.0,
            description: "the unfiltered control subscription must receive every published " +
                "message; without this barrier an absence at a filtered arm proves " +
                "nothing");
        var latency = Math.Max(controlSeen.Values.Max() - started, 1.0);
        var ceiling = Math.Max(60.0, 3.0 * latency + 20.0);
        foreach (var (label, (url, expected)) in arms)
        {
            await AwaitBodies(ctx, mono, url, expected, timeoutS: ceiling,
                description: $"the {label} arm must deliver exactly {List(expected.Order(StringComparer.Ordinal))}",
                forbidden: [.. published.Except(expected)]);
        }

        var window = Math.Max(3.0 * latency, 20.0);
        var leaked = await Drain(ctx, arms.ToDictionary(kv => kv.Key, kv => kv.Value.Url), windowS: window);
        Ensure(leaked.Values.All(l => l.Count == 0),
            $"after every arm had received its matching messages, a {window:F0}s window (>=3x " +
            $"the measured control latency of {latency:F1}s) must yield nothing more; got " +
            "{" + string.Join(", ", leaked.Select(kv => $"'{kv.Key}': {List(kv.Value)}")) + "} -- a body here is a withheld message delivered late");
        foreach (var (label, (url, _)) in arms)
        {
            var approx = int.Parse((await sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest
            {
                QueueUrl = url, AttributeNames = ["ApproximateNumberOfMessages"],
            })).Attributes!["ApproximateNumberOfMessages"], CultureInfo.InvariantCulture);
            Ensure(approx == 0,
                $"second observable for the absence: the {label} queue reports " +
                $"ApproximateNumberOfMessages={approx}, expected 0");
        }
    }
}
