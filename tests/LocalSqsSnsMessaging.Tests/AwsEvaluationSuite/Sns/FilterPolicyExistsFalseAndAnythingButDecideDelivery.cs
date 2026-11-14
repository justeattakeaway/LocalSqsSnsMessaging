// Translated from behavioral/sns/check_filter_policy_exists_false_and_anything_but_decide_delivery.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Globalization;
using System.Text.Json.Nodes;
using Amazon.SimpleNotificationService.Model;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;
using SnsMessageAttributeValue = Amazon.SimpleNotificationService.Model.MessageAttributeValue;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sns;

/// <summary>sns.filter-policy-exists-false-and-anything-but-decide-delivery</summary>
[Category("AwsEvaluationSuite")]
public sealed class FilterPolicyExistsFalseAndAnythingButDecideDelivery
{
    private const string Scenario = "sns-filter-polarity";

    /// <summary>
    /// Over four messages, `exists: false` delivers exactly those without a coupon attribute and
    /// list `anything-but` exactly those whose channel is not listed; flipping to `exists: true`
    /// then delivers the withheld coupon-bearing message.
    ///
    /// Reference: https://docs.aws.amazon.com/sns/latest/dg/sns-subscription-filter-policies.html
    /// </summary>
    [Test]
    [DisplayName("sns.filter-policy-exists-false-and-anything-but-decide-delivery")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sns = ctx.Sns;
        var sqs = ctx.Sqs;
        var topicArn = await CreateTopic(ctx);

        var (controlUrl, controlArn) = await CreateQueueForTopic(ctx, topicArn);
        var (existsUrl, existsArn) = await CreateQueueForTopic(ctx, topicArn);
        var (abUrl, abArn) = await CreateQueueForTopic(ctx, topicArn);

        await SubscribeAndConfirm(ctx, topicArn, controlArn,
            attributes: new() { ["RawMessageDelivery"] = "true" });
        var existsSub = await SubscribeAndConfirm(
            ctx, topicArn, existsArn,
            attributes: new()
            {
                ["RawMessageDelivery"] = "true",
                ["FilterPolicy"] = """{"coupon": [{"exists": false}]}""",
            });
        await SubscribeAndConfirm(
            ctx, topicArn, abArn,
            attributes: new()
            {
                ["RawMessageDelivery"] = "true",
                ["FilterPolicy"] = """{"channel": [{"anything-but": ["fax", "pager"]}]}""",
            });

        var m1 = FidelityContext.UniqueName("sim2real-behav-polarity-nocoupon-sms-");
        var m2 = FidelityContext.UniqueName("sim2real-behav-polarity-coupon-sms-");
        var m3 = FidelityContext.UniqueName("sim2real-behav-polarity-nocoupon-fax-");
        var m4 = FidelityContext.UniqueName("sim2real-behav-polarity-coupon-fax-");
        (string Marker, Dictionary<string, SnsMessageAttributeValue> Attributes)[] matrix =
        [
            (m1, new() { ["channel"] = S("sms") }),
            (m2, new() { ["channel"] = S("sms"), ["coupon"] = S("SAVE10") }),
            (m3, new() { ["channel"] = S("fax") }),
            (m4, new() { ["channel"] = S("fax"), ["coupon"] = S("SAVE10") }),
        ];

        var started = FidelityTime.UtcNow;
        foreach (var (marker, attributes) in matrix)
        {
            await sns.PublishAsync(new PublishRequest { TopicArn = topicArn, Message = marker, MessageAttributes = attributes });
        }

        // ---- barrier: all four complete the trip on the unfiltered twin -----------------------
        var controlSeen = await AwaitBodies(
            ctx, controlUrl, [m1, m2, m3, m4], timeoutS: 120.0,
            description: "the unfiltered twin must receive all four messages; without this barrier " +
                         "an absence at either filtered arm proves nothing");
        var latency = Math.Max((controlSeen.Values.Max() - started).TotalSeconds, 1.0);
        var filteredCeiling = Math.Max(60.0, 3.0 * latency + 20.0);

        // ---- the exists:false arm: exactly {M1, M3} -------------------------------------------
        await AwaitBodies(
            ctx, existsUrl, [m1, m3], timeoutS: filteredCeiling,
            description: "{\"coupon\": [{\"exists\": false}]} must deliver exactly the two " +
                         "messages that carry no coupon attribute",
            forbidden: [m2, m4]);

        // ---- the anything-but arm: exactly {M1, M2} -------------------------------------------
        await AwaitBodies(
            ctx, abUrl, [m1, m2], timeoutS: filteredCeiling,
            description: "{\"channel\": [{\"anything-but\": [\"fax\", \"pager\"]}]} must " +
                         "deliver exactly the two messages whose channel is neither",
            forbidden: [m3, m4]);

        // ---- both absences held over one window calibrated from the measured latency ----------
        var window = Math.Max(3.0 * latency, 15.0);
        var leaked = await DrainBoth(ctx, new() { ["exists:false arm"] = existsUrl, ["anything-but arm"] = abUrl },
            windowS: window);
        Ensure(leaked["exists:false arm"].Count == 0 && leaked["anything-but arm"].Count == 0,
            $"after both arms had received their matching messages, a {window.ToString("F0", CultureInfo.InvariantCulture)}s window " +
            $"(>=3x the measured control latency of {latency.ToString("F1", CultureInfo.InvariantCulture)}s) must yield nothing more; got " +
            $"{{{string.Join(", ", leaked.Select(kv => $"'{kv.Key}': {Repr(kv.Value)}"))}}} — a non-empty list here is a withheld message arriving late, which is " +
            "over-delivery, not slowness");
        foreach (var (label, url) in new[] { ("exists:false", existsUrl), ("anything-but", abUrl) })
        {
            var approx = int.Parse((await sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest
            {
                QueueUrl = url,
                AttributeNames = ["ApproximateNumberOfMessages"],
            })).Attributes!["ApproximateNumberOfMessages"], CultureInfo.InvariantCulture);
            Ensure(approx == 0,
                "second independent observable for the absence: expected " +
                $"ApproximateNumberOfMessages == 0 on the {label} queue, got {approx}");
        }

        // ---- repair exactly one thing: flip the polarity and DEMAND the withheld message ------
        await SetFilterPolicy(ctx, existsSub, JsonNode.Parse("""{"coupon": [{"exists": true}]}""")!);
        await RepublishUntilDelivered(
            ctx, topicArn, existsUrl, m2, new Dictionary<string, SnsMessageAttributeValue>(matrix[1].Attributes),
            attempts: 3, perAttemptS: Math.Max(30.0, 2.0 * latency),
            description: "after exists is flipped from false to true, the identical " +
                         "coupon-bearing message must be delivered to the same subscription — " +
                         "this is what proves the operator was evaluated rather than the " +
                         "subscription being broken");

        var stored = JsonNode.Parse((await sns.GetSubscriptionAttributesAsync(new GetSubscriptionAttributesRequest { SubscriptionArn = existsSub }))
            .Attributes["FilterPolicy"]);
        Ensure(JsonNode.DeepEquals(stored, JsonNode.Parse("""{"coupon": [{"exists": true}]}""")),
            $"expected the flipped policy to be the stored document, got {stored?.ToJsonString() ?? "None"}");
    }

    private static string Repr(IEnumerable<string> values) => $"[{string.Join(", ", values.Select(v => $"'{v}'"))}]";

    /// <summary>A single String-typed message attribute value.</summary>
    private static SnsMessageAttributeValue S(string value) => new() { DataType = "String", StringValue = value };

    /// <summary>Replace a subscription's FilterPolicy and poll until an independent read matches.</summary>
    private static async Task SetFilterPolicy(FidelityContext ctx, string subArn, JsonNode policy, double timeoutS = 60.0)
    {
        var sns = ctx.Sns;
        await sns.SetSubscriptionAttributesAsync(new SetSubscriptionAttributesRequest
        {
            SubscriptionArn = subArn,
            AttributeName = "FilterPolicy",
            AttributeValue = policy.ToJsonString(),
        });
        await PollUntil(
            async () => (await sns.GetSubscriptionAttributesAsync(new GetSubscriptionAttributesRequest { SubscriptionArn = subArn }))
                .Attributes?.GetValueOrDefault("FilterPolicy"),
            raw => !string.IsNullOrEmpty(raw) && JsonNode.DeepEquals(JsonNode.Parse(raw), policy),
            timeoutS: timeoutS,
            intervalS: 2.0,
            description: $"GetSubscriptionAttributes reflects the FilterPolicy {policy.ToJsonString()}");
    }

    /// <summary>Alternate long polls across several queues for one shared window; return the bodies.</summary>
    private static async Task<Dictionary<string, List<string>>> DrainBoth(FidelityContext ctx, Dictionary<string, string> queues, double windowS)
    {
        var sqs = ctx.Sqs;
        var @out = queues.Keys.ToDictionary(label => label, _ => new List<string>());
        var deadline = FidelityTime.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(windowS))
        {
            foreach (var (label, url) in queues)
            {
                var resp = await sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = url, MaxNumberOfMessages = 10, WaitTimeSeconds = 2 });
                foreach (var msg in resp.Messages ?? [])
                {
                    await sqs.DeleteMessageAsync(new DeleteMessageRequest { QueueUrl = url, ReceiptHandle = msg.ReceiptHandle });
                    @out[label].Add(msg.Body);
                }
            }
        }

        return @out;
    }

    /// <summary>
    /// Publish the same marker up to `attempts` times and RAISE if it never lands.
    ///
    /// A FilterPolicy update is eventually consistent on the delivery path, hence the retries.
    /// </summary>
    private static async Task RepublishUntilDelivered(FidelityContext ctx, string topicArn, string queueUrl, string marker,
        Dictionary<string, SnsMessageAttributeValue> attributes, int attempts, double perAttemptS, string description)
    {
        var sns = ctx.Sns;
        var sqs = ctx.Sqs;
        var seen = new List<string>();
        for (var i = 0; i < attempts; i++)
        {
            await sns.PublishAsync(new PublishRequest { TopicArn = topicArn, Message = marker, MessageAttributes = attributes });
            var deadline = FidelityTime.StartNew();
            while (deadline.Elapsed < TimeSpan.FromSeconds(perAttemptS))
            {
                var resp = await sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = queueUrl, MaxNumberOfMessages = 10, WaitTimeSeconds = 5 });
                foreach (var msg in resp.Messages ?? [])
                {
                    await sqs.DeleteMessageAsync(new DeleteMessageRequest { QueueUrl = queueUrl, ReceiptHandle = msg.ReceiptHandle });
                    seen.Add(msg.Body);
                }

                if (seen.Contains(marker))
                {
                    return;
                }
            }
        }

        throw Fail($"{description}: '{marker}' was never delivered across {attempts} republishes of " +
            $"{perAttemptS.ToString("F0", CultureInfo.InvariantCulture)}s each; bodies seen: {Repr(seen)}");
    }

    private static List<Amazon.SimpleNotificationService.Model.Tag> TagsKv() =>
        [new() { Key = "sim2real", Value = Scenario }, new() { Key = "sim2real:managed", Value = "true" }];

    /// <summary>CreateTopic (tagged + prefix-named) and register DeleteTopic immediately.</summary>
    private static async Task<string> CreateTopic(FidelityContext ctx, Dictionary<string, string>? attributes = null)
    {
        var sns = ctx.Sns;
        var name = FidelityContext.UniqueName("sim2real-behav-sns-t-");
        var arn = (await sns.CreateTopicAsync(new CreateTopicRequest
        {
            Name = name,
            Attributes = new Dictionary<string, string>(attributes ?? []),
            Tags = TagsKv(),
        })).TopicArn;
        ctx.OnCleanup(() => ctx.Sns.DeleteTopicAsync(new DeleteTopicRequest { TopicArn = arn }));
        return arn;
    }

    /// <summary>Create a real SQS queue whose policy lets exactly this topic publish to it.</summary>
    private static async Task<(string Url, string Arn)> CreateQueueForTopic(FidelityContext ctx, string topicArn)
    {
        var sqs = ctx.Sqs;
        var name = FidelityContext.UniqueName("sim2real-behav-sns-q-");
        var url = (await sqs.CreateQueueAsync(new CreateQueueRequest
        {
            QueueName = name,
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
            timeoutS: timeoutS,
            intervalS: 2.0,
            description: $"subscription {subArn} leaves PendingConfirmation");
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
    ///
    /// Returns {body: time first seen} so callers can calibrate an absence window.
    /// </summary>
    private static async Task<Dictionary<string, DateTimeOffset>> AwaitBodies(FidelityContext ctx, string queueUrl, HashSet<string> wanted,
        double timeoutS, string description, HashSet<string>? forbidden = null)
    {
        var sqs = ctx.Sqs;
        var seen = new Dictionary<string, DateTimeOffset>();
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
}
