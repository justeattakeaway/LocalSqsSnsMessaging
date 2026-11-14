// Translated from behavioral/sns/check_filter_policy_numeric_operator_boundary_decides_delivery.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Globalization;
using System.Text.Json.Nodes;
using Amazon.SimpleNotificationService.Model;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;
using SnsMessageAttributeValue = Amazon.SimpleNotificationService.Model.MessageAttributeValue;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sns;

/// <summary>sns.filter-policy-numeric-operator-boundary-decides-delivery</summary>
[Category("AwsEvaluationSuite")]
public sealed class FilterPolicyNumericOperatorBoundaryDecidesDelivery
{
    private const string Scenario = "sns-filter-numeric";

    /// <summary>
    /// With <c>{"price": [{"numeric": [">", 100]}]}</c> price=101 is delivered and price=100 is withheld
    /// while an unfiltered twin receives both, and widening the operator to <c>&gt;=</c> makes the previously
    /// withheld subscription receive: the numeric operator decides delivery at the exact boundary.
    /// </summary>
    [Test]
    [DisplayName("sns.filter-policy-numeric-operator-boundary-decides-delivery")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sns = ctx.Sns;
        var sqs = ctx.Sqs;
        // time.monotonic(): seconds on one virtual clock shared by the helpers.
        var mono = FidelityTime.StartNew();
        var topicArn = await CreateTopic(ctx);

        var (controlUrl, controlArn) = await CreateQueueForTopic(ctx, topicArn);
        var (filteredUrl, filteredArn) = await CreateQueueForTopic(ctx, topicArn);

        // The control arm carries NO filter policy: it is the twin doing the same work at the same
        // moment, and it is what makes an absence at the filtered arm mean "withheld" rather than
        // "nothing was ever published".
        await SubscribeAndConfirm(ctx, topicArn, controlArn, attributes: new() { ["RawMessageDelivery"] = "true" });
        var filteredSub = await SubscribeAndConfirm(ctx, topicArn, filteredArn, attributes: new()
        {
            ["RawMessageDelivery"] = "true",
            ["FilterPolicy"] = Policy(">").ToJsonString(),
        });

        var atBoundary = FidelityContext.UniqueName("sim2real-behav-numeric-at-100-");
        var above = FidelityContext.UniqueName("sim2real-behav-numeric-at-101-");

        // Published boundary-first, so in-order delivery would surface the withheld message before
        // the delivered one at the filtered arm.
        var started = mono.Elapsed.TotalSeconds;
        await sns.PublishAsync(new PublishRequest { TopicArn = topicArn, Message = atBoundary, MessageAttributes = Number("price", "100") });
        await sns.PublishAsync(new PublishRequest { TopicArn = topicArn, Message = above, MessageAttributes = Number("price", "101") });

        // ---- barrier: both messages complete the trip on the unfiltered twin ------------------
        var controlSeen = await AwaitBodies(ctx, mono, controlUrl, [atBoundary, above], timeoutS: 120.0,
            description: "the unfiltered twin subscription must receive BOTH messages; without " +
                "this barrier an absence at the filtered arm proves nothing");
        var latency = Math.Max(controlSeen.Values.Max() - started, 1.0);

        // ---- the delivered half, with the withheld half a hard failure if it appears ----------
        await AwaitBodies(ctx, mono, filteredUrl, [above], timeoutS: Math.Max(120.0, 3.0 * latency + 30.0),
            description: "price=101 must satisfy {\"numeric\": [\">\", 100]} and be delivered",
            forbidden: [atBoundary]);

        // ---- the withheld half, over a window calibrated from the measured control latency ----
        var window = Math.Max(3.0 * latency, 15.0);
        var leaked = await Drain(ctx, filteredUrl, windowS: window);
        Ensure(leaked.Count == 0,
            $"price=100 must NOT satisfy {{\"numeric\": [\">\", 100]}}: the filtered subscription " +
            $"received {List(leaked)} during a {window:F0}s window (>=3x the measured control latency " +
            $"of {latency:F1}s) that began after price=101 had already arrived");
        var approx = int.Parse((await sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest
        {
            QueueUrl = filteredUrl, AttributeNames = ["ApproximateNumberOfMessages"],
        })).Attributes!["ApproximateNumberOfMessages"], CultureInfo.InvariantCulture);
        Ensure(approx == 0,
            "second independent observable for the absence: expected ApproximateNumberOfMessages " +
            $"== 0 on the filtered queue, got {approx}");

        // ---- repair exactly the one withheld thing and DEMAND the delivery -------------------
        await SetFilterPolicy(ctx, filteredSub, Policy(">="));
        await RepublishUntilDelivered(ctx, topicArn, filteredUrl, atBoundary, Number("price", "100"),
            attempts: 5, perAttemptS: Math.Max(30.0, 2.0 * latency),
            description: "after the operator is widened from > to >=, the identical price=100 " +
                "message must be delivered to the same subscription — this is what " +
                "proves the boundary was the discriminator rather than the numeric " +
                "operator suppressing everything");

        // ---- independent read of the control document, through a different operation ----------
        var stored = JsonNode.Parse((await sns.GetSubscriptionAttributesAsync(new GetSubscriptionAttributesRequest { SubscriptionArn = filteredSub }))
            .Attributes!["FilterPolicy"]);
        Ensure(JsonNode.DeepEquals(stored, Policy(">=")),
            $"expected the widened policy to be the stored document, got {stored?.ToJsonString()}");
    }

    // ===== helpers =====

    /// <summary><c>{"price": [{"numeric": [op, 100]}]}</c>.</summary>
    private static JsonObject Policy(string op) =>
        new() { ["price"] = new JsonArray(new JsonObject { ["numeric"] = new JsonArray(op, 100) }) };

    private static string List(IEnumerable<string> items) => "[" + string.Join(", ", items.Select(s => $"'{s}'")) + "]";

    /// <summary>
    /// A single Number-typed message attribute. SNS compares <c>numeric</c> policy operators only
    /// against Number attributes (documented in Filter policy constraints in Amazon SNS).
    /// </summary>
    private static Dictionary<string, SnsMessageAttributeValue> Number(string name, string value) =>
        new() { [name] = new SnsMessageAttributeValue { DataType = "Number", StringValue = value } };

    /// <summary>Replace a subscription's FilterPolicy and poll until an independent read matches.</summary>
    private static async Task SetFilterPolicy(FidelityContext ctx, string subArn, JsonObject policy, double timeoutS = 60.0)
    {
        var sns = ctx.Sns;
        await sns.SetSubscriptionAttributesAsync(new SetSubscriptionAttributesRequest
        {
            SubscriptionArn = subArn, AttributeName = "FilterPolicy", AttributeValue = policy.ToJsonString(),
        });
        await PollUntil(
            async () => (await sns.GetSubscriptionAttributesAsync(new GetSubscriptionAttributesRequest { SubscriptionArn = subArn }))
                .Attributes?.GetValueOrDefault("FilterPolicy"),
            raw => !string.IsNullOrEmpty(raw) && JsonNode.DeepEquals(JsonNode.Parse(raw), policy),
            timeoutS: timeoutS,
            intervalS: 2.0,
            description: $"GetSubscriptionAttributes reflects the FilterPolicy {policy.ToJsonString()}");
    }

    /// <summary>
    /// Publish the same marker up to <c>attempts</c> times, draining between attempts, and RAISE
    /// if it never lands.
    /// </summary>
    /// <remarks>
    /// A FilterPolicy update is eventually consistent on the delivery path, so the bounded
    /// republish is the retry that makes the repaired-path assertion correct rather than racy.
    /// The marker and its attributes are constant across attempts, so the assertion stays exact.
    /// </remarks>
    private static async Task RepublishUntilDelivered(FidelityContext ctx, string topicArn, string queueUrl, string marker,
        Dictionary<string, SnsMessageAttributeValue> attributes, int attempts, double perAttemptS, string description)
    {
        var sns = ctx.Sns;
        var sqs = ctx.Sqs;
        var seen = new List<string>();
        for (var attempt = 0; attempt < attempts; attempt++)
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
            $"{perAttemptS:F0}s each; bodies seen: {List(seen)}");
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
            Name = name, Attributes = new Dictionary<string, string>(attributes ?? []), Tags = TagsKv(),
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
    /// </summary>
    /// <remarks>
    /// Returns {body: monotonic time it was first seen} so a caller can measure the real
    /// latency of this wiring and calibrate an absence window from it. A body in <c>forbidden</c>
    /// that arrives raises immediately: it is a leak, and swallowing it would hide the very
    /// thing the caller is about to assert.
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
            // The empty default only ever means "poll again"; the deadline below RAISES, so an
            // absence can never pass.
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
    /// Long-poll for the whole window and return EVERY body seen, so the caller can assert
    /// on leakage rather than discard it. The empty default is correct here: it feeds an
    /// equality-to-empty assertion.
    /// </summary>
    private static async Task<List<string>> Drain(FidelityContext ctx, string queueUrl, double windowS)
    {
        var sqs = ctx.Sqs;
        var bodies = new List<string>();
        var deadline = FidelityTime.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(windowS))
        {
            var resp = await sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = queueUrl, MaxNumberOfMessages = 10, WaitTimeSeconds = 5 });
            foreach (var msg in resp.Messages ?? [])
            {
                await sqs.DeleteMessageAsync(new DeleteMessageRequest { QueueUrl = queueUrl, ReceiptHandle = msg.ReceiptHandle });
                bodies.Add(msg.Body);
            }
        }

        return bodies;
    }
}
