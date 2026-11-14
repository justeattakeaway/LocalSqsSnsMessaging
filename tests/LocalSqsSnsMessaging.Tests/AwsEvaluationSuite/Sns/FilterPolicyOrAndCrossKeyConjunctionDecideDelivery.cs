// Translated from behavioral/sns/check_filter_policy_or_and_cross_key_conjunction_decide_delivery.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Globalization;
using System.Text.Json.Nodes;
using Amazon.SimpleNotificationService.Model;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;
using SnsMessageAttributeValue = Amazon.SimpleNotificationService.Model.MessageAttributeValue;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sns;

/// <summary>sns.filter-policy-or-and-cross-key-conjunction-decide-delivery</summary>
[Category("AwsEvaluationSuite")]
public sealed class FilterPolicyOrAndCrossKeyConjunctionDecideDelivery
{
    private const string Scenario = "sns-filter-boolean";

    /// <summary>
    /// A filter policy ANDs keys and ORs values within a key, and `$or` makes the keys a
    /// disjunction: over six messages, AND {color:[red,blue], size:[L]} delivers exactly red/L and
    /// blue/L, and $or [{color:[red]},{size:[L]}] every message but green/S.
    /// </summary>
    [Test]
    [DisplayName("sns.filter-policy-or-and-cross-key-conjunction-decide-delivery")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sns = ctx.Sns;
        var topicArn = await CreateTopic(ctx);

        var (controlUrl, controlArn) = await CreateQueueForTopic(ctx, topicArn);
        await SubscribeAndConfirm(ctx, topicArn, controlArn, attributes: new() { ["RawMessageDelivery"] = "true" });

        (string Label, JsonObject Policy)[] policies =
        [
            ("AND", new JsonObject { ["color"] = new JsonArray("red", "blue"), ["size"] = new JsonArray("L") }),
            ("$or", new JsonObject
            {
                ["$or"] = new JsonArray(
                    new JsonObject { ["color"] = new JsonArray("red") },
                    new JsonObject { ["size"] = new JsonArray("L") }),
            }),
        ];
        var armUrls = new Dictionary<string, string>();
        foreach (var (label, policy) in policies)
        {
            var (url, arn) = await CreateQueueForTopic(ctx, topicArn);
            await SubscribeAndConfirm(ctx, topicArn, arn, attributes: new()
            {
                ["RawMessageDelivery"] = "true", ["FilterPolicy"] = policy.ToJsonString(),
            });
            armUrls[label] = url;
        }

        var marks = Enumerable.Range(1, 6).Select(i => FidelityContext.UniqueName($"sim2real-behav-bool-m{i}-")).ToList();
        var (m1, m2, m3, m4, m5, m6) = (marks[0], marks[1], marks[2], marks[3], marks[4], marks[5]);
        var attributes = new List<(string Marker, Dictionary<string, SnsMessageAttributeValue> Attrs)>
        {
            (m1, new() { ["color"] = S("red"), ["size"] = S("L") }),
            (m2, new() { ["color"] = S("red"), ["size"] = S("S") }),
            (m3, new() { ["color"] = S("green"), ["size"] = S("L") }),
            (m4, new() { ["color"] = S("blue"), ["size"] = S("L") }),
            (m5, new() { ["color"] = S("green"), ["size"] = S("S") }),
            (m6, new() { ["color"] = S("red") }),
        };

        var started = FidelityTime.UtcNow;
        foreach (var (marker, attrs) in attributes)
        {
            await sns.PublishAsync(new PublishRequest { TopicArn = topicArn, Message = marker, MessageAttributes = attrs });
        }

        var expected = new Dictionary<string, HashSet<string>> { ["AND"] = [m1, m4], ["$or"] = [m1, m2, m3, m4, m6] };
        await GradeMatrix(ctx, controlUrl,
            policies.ToDictionary(p => p.Label, p => (armUrls[p.Label], expected[p.Label])),
            marks.ToHashSet(), started);
    }

    // Helpers: an SQS queue whose policy admits exactly this topic, a confirmed subscription, and
    // long-poll waits that raise on their deadline so a timeout can never read as a pass.

    /// <summary>A String-typed message attribute value.</summary>
    private static SnsMessageAttributeValue S(string value) => new() { DataType = "String", StringValue = value };

    private static string Repr(IEnumerable<string> items) => "[" + string.Join(", ", items.Select(i => $"'{i}'")) + "]";

    private static List<Amazon.SimpleNotificationService.Model.Tag> TagsKv() =>
        [new() { Key = "sim2real", Value = Scenario }, new() { Key = "sim2real:managed", Value = "true" }];

    /// <summary>CreateTopic (tagged + prefix-named) and register DeleteTopic immediately.</summary>
    private static async Task<string> CreateTopic(FidelityContext ctx)
    {
        var name = FidelityContext.UniqueName("sim2real-behav-sns-t-");
        var arn = (await ctx.Sns.CreateTopicAsync(new CreateTopicRequest { Name = name, Attributes = [], Tags = TagsKv() })).TopicArn;
        ctx.OnCleanup(() => ctx.Sns.DeleteTopicAsync(new DeleteTopicRequest { TopicArn = arn }));
        return arn;
    }

    /// <summary>Create a real SQS queue whose policy lets exactly this topic send to it.</summary>
    private static async Task<(string Url, string Arn)> CreateQueueForTopic(FidelityContext ctx, string topicArn)
    {
        var sqs = ctx.Sqs;
        var name = FidelityContext.UniqueName("sim2real-behav-sns-q-");
        var url = (await sqs.CreateQueueAsync(new CreateQueueRequest
        {
            QueueName = name,
            Attributes = [],
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
    /// Returns {body: monotonic time first seen}. A body in <c>forbidden</c> that arrives raises at
    /// once: it is a leak, and swallowing it would hide what the caller is about to assert.
    /// </summary>
    private static async Task<Dictionary<string, DateTimeOffset>> AwaitBodies(FidelityContext ctx, string queueUrl, HashSet<string> wanted,
        double timeoutS, string description, HashSet<string>? forbidden = null)
    {
        var sqs = ctx.Sqs;
        var seen = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);
        var outstanding = new HashSet<string>(wanted, StringComparer.Ordinal);
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
                    seen[body] = FidelityTime.UtcNow;
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
        HashSet<string> published, DateTimeOffset started)
    {
        var sqs = ctx.Sqs;
        var controlSeen = await AwaitBodies(ctx, controlUrl, published, timeoutS: 120.0,
            description: "the unfiltered control subscription must receive every published " +
                "message; without this barrier an absence at a filtered arm proves " +
                "nothing");
        var latency = Math.Max((controlSeen.Values.Max() - started).TotalSeconds, 1.0);
        var ceiling = Math.Max(60.0, (3.0 * latency) + 20.0);
        foreach (var (label, (url, expected)) in arms)
        {
            await AwaitBodies(ctx, url, expected, timeoutS: ceiling,
                description: $"the {label} arm must deliver exactly {Repr(expected.Order(StringComparer.Ordinal))}",
                forbidden: published.Except(expected).ToHashSet());
        }

        var window = Math.Max(3.0 * latency, 20.0);
        var leaked = await Drain(ctx, arms.ToDictionary(a => a.Key, a => a.Value.Url), windowS: window);
        Ensure(leaked.Values.All(l => l.Count == 0),
            $"after every arm had received its matching messages, a {window.ToString("F0", CultureInfo.InvariantCulture)}s window (>=3x " +
            $"the measured control latency of {latency.ToString("F1", CultureInfo.InvariantCulture)}s) must yield nothing more; got " +
            $"{{{string.Join(", ", leaked.Select(kv => $"'{kv.Key}': {Repr(kv.Value)}"))}}} -- a body here is a withheld message delivered late");
        foreach (var (label, (url, _)) in arms)
        {
            var approx = int.Parse((await sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest
            {
                QueueUrl = url, AttributeNames = ["ApproximateNumberOfMessages"],
            })).Attributes!["ApproximateNumberOfMessages"], CultureInfo.InvariantCulture);
            Ensure(approx == 0,
                "second observable for the absence: the " + label + " queue reports " +
                $"ApproximateNumberOfMessages={approx}, expected 0");
        }
    }
}
