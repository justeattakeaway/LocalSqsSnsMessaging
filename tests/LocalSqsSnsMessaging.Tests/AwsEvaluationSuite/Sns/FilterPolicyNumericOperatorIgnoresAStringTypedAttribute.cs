// Translated from behavioral/sns/check_filter_policy_numeric_operator_ignores_a_string_typed_attribute.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Globalization;
using System.Text.Json.Nodes;
using Amazon.SimpleNotificationService.Model;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;
using SnsMessageAttributeValue = Amazon.SimpleNotificationService.Model.MessageAttributeValue;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sns;

/// <summary>sns.filter-policy-numeric-operator-ignores-a-string-typed-attribute</summary>
[Category("AwsEvaluationSuite")]
public sealed class FilterPolicyNumericOperatorIgnoresAStringTypedAttribute
{
    private const string Scenario = "sns-filter-attr-typing";

    /// <summary>
    /// Attribute matching is typed: {"numeric": [">", 10]} delivers only Number 20 (not String "20"
    /// or Number 5), and the exact string ["20"] delivers only String "20", so each subscriber
    /// receives exactly that one message.
    /// </summary>
    [Test]
    [DisplayName("sns.filter-policy-numeric-operator-ignores-a-string-typed-attribute")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sns = ctx.Sns;
        var topicArn = await CreateTopic(ctx);

        var (controlUrl, controlArn) = await CreateQueueForTopic(ctx, topicArn);
        await SubscribeAndConfirm(ctx, topicArn, controlArn, attributes: new() { ["RawMessageDelivery"] = "true" });

        var policies = new List<(string Label, string Policy)>
        {
            ("numeric", """{"price": [{"numeric": [">", 10]}]}"""),
            ("exact-string", """{"price": ["20"]}"""),
        };
        var armUrls = new Dictionary<string, string>();
        foreach (var (label, policy) in policies)
        {
            var (url, arn) = await CreateQueueForTopic(ctx, topicArn);
            await SubscribeAndConfirm(ctx, topicArn, arn,
                attributes: new() { ["RawMessageDelivery"] = "true", ["FilterPolicy"] = policy });
            armUrls[label] = url;
        }

        var m1 = FidelityContext.UniqueName("sim2real-behav-typing-num20-");
        var m2 = FidelityContext.UniqueName("sim2real-behav-typing-str20-");
        var m3 = FidelityContext.UniqueName("sim2real-behav-typing-num5-");
        var attributes = new List<(string Marker, Dictionary<string, SnsMessageAttributeValue> Attrs)>
        {
            (m1, new() { ["price"] = N("20") }),
            (m2, new() { ["price"] = S("20") }),
            (m3, new() { ["price"] = N("5") }),
        };

        var started = Monotonic();
        foreach (var (marker, attrs) in attributes)
        {
            await sns.PublishAsync(new PublishRequest { TopicArn = topicArn, Message = marker, MessageAttributes = attrs });
        }

        var expected = new Dictionary<string, HashSet<string>> { ["numeric"] = [m1], ["exact-string"] = [m2] };
        await GradeMatrix(ctx, controlUrl,
            policies.ToDictionary(p => p.Label, p => (armUrls[p.Label], expected[p.Label])),
            attributes.Select(a => a.Marker).ToHashSet(), started);
    }

    // Helpers: an SQS queue whose policy admits exactly this topic, a confirmed subscription, and
    // long-poll waits that raise on their deadline so a timeout can never read as a pass.

    /// <summary>time.monotonic(), on the check's virtual clock.</summary>
    private static double Monotonic() => FidelityTime.UtcNow.ToUnixTimeMilliseconds() / 1000.0;

    private static string Repr(IEnumerable<string> items) => "[" + string.Join(", ", items.Select(s => $"'{s}'")) + "]";

    private static string Sorted(IEnumerable<string> items) => Repr(items.Order(StringComparer.Ordinal));

    private static string F(double value, string format) => value.ToString(format, CultureInfo.InvariantCulture);

    /// <summary>A String-typed message attribute value.</summary>
    private static SnsMessageAttributeValue S(string value) => new() { DataType = "String", StringValue = value };

    /// <summary>A Number-typed message attribute value.</summary>
    private static SnsMessageAttributeValue N(string value) => new() { DataType = "Number", StringValue = value };

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
                throw Fail($"{description}: {Sorted(outstanding)} never arrived within {timeoutS}s");
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
    /// The unfiltered control receives every published body first; that arrival is the barrier.
    /// Each arm must then receive exactly its expected set (a body outside it raises at once),
    /// and a shared window calibrated from the measured control latency must yield nothing more
    /// on any arm, read twice: by draining, and by ApproximateNumberOfMessages.
    /// </summary>
    private static async Task GradeMatrix(FidelityContext ctx, string controlUrl, Dictionary<string, (string Url, HashSet<string> Expected)> arms,
        HashSet<string> published, double started)
    {
        var sqs = ctx.Sqs;
        var controlSeen = await AwaitBodies(ctx, controlUrl, published, timeoutS: 120.0,
            description: "the unfiltered control subscription must receive every published " +
                "message; without this barrier an absence at a filtered arm proves " +
                "nothing");
        var latency = Math.Max(controlSeen.Values.Max() - started, 1.0);
        var ceiling = Math.Max(60.0, 3.0 * latency + 20.0);
        foreach (var (label, (url, expected)) in arms)
        {
            await AwaitBodies(ctx, url, expected, timeoutS: ceiling,
                description: $"the {label} arm must deliver exactly {Sorted(expected)}",
                forbidden: published.Except(expected).ToHashSet());
        }

        var window = Math.Max(3.0 * latency, 20.0);
        var leaked = await Drain(ctx, arms.ToDictionary(a => a.Key, a => a.Value.Url), windowS: window);
        Ensure(leaked.Values.All(l => l.Count == 0),
            $"after every arm had received its matching messages, a {F(window, "F0")}s window (>=3x " +
            $"the measured control latency of {F(latency, "F1")}s) must yield nothing more; got " +
            $"{{{string.Join(", ", leaked.Select(kv => $"'{kv.Key}': {Repr(kv.Value)}"))}}} -- a body here is a withheld message delivered late");
        foreach (var (label, (url, _)) in arms)
        {
            var approx = int.Parse((await sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest
            {
                QueueUrl = url,
                AttributeNames = ["ApproximateNumberOfMessages"],
            })).Attributes["ApproximateNumberOfMessages"], CultureInfo.InvariantCulture);
            Ensure(approx == 0,
                "second observable for the absence: the " + label + " queue reports " +
                $"ApproximateNumberOfMessages={approx}, expected 0");
        }
    }
}
