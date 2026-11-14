// Translated from behavioral/sns__sqs/check_sns_filter_policy_scope_is_orthogonal_and_message_body_matches_nested_keys.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Globalization;
using System.Text.Json.Nodes;
using Amazon.SimpleNotificationService.Model;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;
using SnsMessageAttributeValue = Amazon.SimpleNotificationService.Model.MessageAttributeValue;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.SnsSqs;

/// <summary>sns__sqs.sns-filter-policy-scope-is-orthogonal-and-message-body-matches-nested-keys</summary>
[Category("AwsEvaluationSuite")]
public sealed class SnsFilterPolicyScopeIsOrthogonalAndMessageBodyMatchesNestedKeys
{
    private const string Scenario = "sns-filter-scope";

    /// <summary>
    /// The same policy under MessageAttributes and MessageBody scope selects disjoint halves of the
    /// same two messages (neither scope falls back to the other), and a MessageBody policy evaluates a
    /// nested key with a numeric operator: order.total=150 is delivered and 100 is withheld.
    /// </summary>
    [Test]
    [DisplayName("sns__sqs.sns-filter-policy-scope-is-orthogonal-and-message-body-matches-nested-keys")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sns = ctx.Sns;
        var sqs = ctx.Sqs;
        var clock = FidelityTime.StartNew(); // time.monotonic()
        var topicArn = await CreateTopic(ctx);

        var (controlUrl, controlArn) = await CreateQueueForTopic(ctx, topicArn);
        var (attrUrl, attrArn) = await CreateQueueForTopic(ctx, topicArn);
        var (bodyUrl, bodyArn) = await CreateQueueForTopic(ctx, topicArn);

        var flatPolicy = new JsonObject { ["kind"] = new JsonArray("wanted") }.ToJsonString();
        await SubscribeAndConfirm(ctx, topicArn, controlArn,
            attributes: new() { ["RawMessageDelivery"] = "true" });
        var attrSub = await SubscribeAndConfirm(
            ctx, topicArn, attrArn,
            attributes: new()
            {
                ["RawMessageDelivery"] = "true",
                ["FilterPolicy"] = flatPolicy,
                ["FilterPolicyScope"] = "MessageAttributes",
            });
        var bodySub = await SubscribeAndConfirm(
            ctx, topicArn, bodyArn,
            attributes: new()
            {
                ["RawMessageDelivery"] = "true",
                ["FilterPolicy"] = flatPolicy,
                ["FilterPolicyScope"] = "MessageBody",
            });
        foreach (var (label, subArn, expectedScope) in new[] { ("attribute", attrSub, "MessageAttributes"), ("body", bodySub, "MessageBody") })
        {
            var got = (await sns.GetSubscriptionAttributesAsync(new GetSubscriptionAttributesRequest { SubscriptionArn = subArn })).Attributes?.GetValueOrDefault("FilterPolicyScope");
            Ensure(got == expectedScope,
                $"the {label} arm must be configured with FilterPolicyScope '{expectedScope}' or " +
                $"the whole matrix below is meaningless; GetSubscriptionAttributes read {Repr(got)}");
        }

        // ---- phase 1 --------------------------------------------------------------------------
        var ma = FidelityContext.UniqueName("sim2real-behav-scope-attr-only-");
        var mb = FidelityContext.UniqueName("sim2real-behav-scope-body-only-");
        var started = clock.Elapsed.TotalSeconds;
        await sns.PublishAsync(new PublishRequest
        {
            TopicArn = topicArn,
            Message = new JsonObject { ["kind"] = "unwanted", ["marker"] = ma }.ToJsonString(),
            MessageAttributes = new() { ["kind"] = new SnsMessageAttributeValue { DataType = "String", StringValue = "wanted" } },
        });
        await sns.PublishAsync(new PublishRequest
        {
            TopicArn = topicArn,
            Message = new JsonObject { ["kind"] = "wanted", ["marker"] = mb }.ToJsonString(),
        });

        var controlSeen = await AwaitMarkers(
            ctx, clock, controlUrl, [ma, mb], timeoutS: 90.0,
            description: "the unfiltered barrier subscription must receive both messages; without " +
                "it neither absence below means anything",
            forbidden: []);
        var latency = Math.Max(controlSeen.Values.Max() - started, 1.0);
        var ceiling = Math.Max(60.0, (3.0 * latency) + 20.0);

        await AwaitMarkers(
            ctx, clock, attrUrl, [ma], timeoutS: ceiling,
            description: "MessageAttributes scope must match the ATTRIBUTE kind=wanted and must NOT " +
                "match the body key of the same name",
            forbidden: [mb]);
        await AwaitMarkers(
            ctx, clock, bodyUrl, [mb], timeoutS: ceiling,
            description: "MessageBody scope must match the BODY key kind=wanted and must NOT match " +
                "the message attribute of the same name",
            forbidden: [ma]);
        var window = Math.Max(3.0 * latency, 15.0);
        var leaked = await DrainBoth(ctx, new() { ["attribute arm"] = attrUrl, ["body arm"] = bodyUrl }, windowS: window);
        Ensure(leaked["attribute arm"].Count == 0 && leaked["body arm"].Count == 0,
            $"the two scopes must select DISJOINT halves: a {F(window, 0)}s window (>=3x the measured " +
            $"barrier latency of {F(latency, 1)}s), begun after each arm had received its own " +
            $"message, produced {{{string.Join(", ", leaked.Select(kv => $"'{kv.Key}': {Show(kv.Value)}"))}}} — a late arrival here is a cross-scope match, not " +
            "slowness");
        foreach (var (label, url) in new[] { ("attribute", attrUrl), ("body", bodyUrl) })
        {
            var approx = int.Parse((await sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest
            {
                QueueUrl = url,
                AttributeNames = ["ApproximateNumberOfMessages"],
            })).Attributes!["ApproximateNumberOfMessages"], CultureInfo.InvariantCulture);
            Ensure(approx == 0,
                "second independent observable for the absence: expected " +
                $"ApproximateNumberOfMessages == 0 on the {label} arm, got {approx}");
        }

        // ---- phase 2: a nested key with a numeric operator, still under MessageBody scope -------
        var nested = new JsonObject
        {
            ["order"] = new JsonObject { ["total"] = new JsonArray(new JsonObject { ["numeric"] = new JsonArray(">", 100) }) },
        };
        await sns.SetSubscriptionAttributesAsync(new SetSubscriptionAttributesRequest
        {
            SubscriptionArn = bodySub,
            AttributeName = "FilterPolicy",
            AttributeValue = nested.ToJsonString(),
        });
        await PollUntil(
            async () => (await sns.GetSubscriptionAttributesAsync(new GetSubscriptionAttributesRequest { SubscriptionArn = bodySub })).Attributes?.GetValueOrDefault("FilterPolicy"),
            raw => !string.IsNullOrEmpty(raw) && JsonNode.DeepEquals(JsonNode.Parse(raw), nested),
            timeoutS: 60.0,
            intervalS: 2.0,
            description: "GetSubscriptionAttributes reflects the nested MessageBody FilterPolicy");
        var scopeAfter = (await sns.GetSubscriptionAttributesAsync(new GetSubscriptionAttributesRequest { SubscriptionArn = bodySub })).Attributes?.GetValueOrDefault("FilterPolicyScope");
        Ensure(scopeAfter == "MessageBody",
            "replacing the FilterPolicy must not silently reset FilterPolicyScope; expected " +
            $"'MessageBody', got {Repr(scopeAfter)}");

        var above = FidelityContext.UniqueName("sim2real-behav-scope-nested-151-");
        var atBoundary = FidelityContext.UniqueName("sim2real-behav-scope-nested-100-");
        var phase2Started = clock.Elapsed.TotalSeconds;
        // Boundary-first, so in-order delivery would surface the withheld message first.
        await sns.PublishAsync(new PublishRequest
        {
            TopicArn = topicArn,
            Message = new JsonObject { ["order"] = new JsonObject { ["total"] = 100 }, ["marker"] = atBoundary }.ToJsonString(),
        });
        await sns.PublishAsync(new PublishRequest
        {
            TopicArn = topicArn,
            Message = new JsonObject { ["order"] = new JsonObject { ["total"] = 150 }, ["marker"] = above }.ToJsonString(),
        });

        var controlSeen2 = await AwaitMarkers(
            ctx, clock, controlUrl, [above, atBoundary], timeoutS: 90.0,
            description: "the barrier subscription must receive both nested-body messages before " +
                "the body arm's absence is read",
            forbidden: []);
        var latency2 = Math.Max(controlSeen2.Values.Max() - phase2Started, 1.0);
        await AwaitMarkers(
            ctx, clock, bodyUrl, [above], timeoutS: Math.Max(45.0, (3.0 * latency2) + 15.0),
            description: "a nested MessageBody policy must match order.total=150 against " +
                "{\"numeric\": [\">\", 100]}",
            forbidden: [atBoundary]);
        var window2 = Math.Max(3.0 * latency2, 15.0);
        var leaked2 = await Drain(ctx, bodyUrl, windowS: window2);
        Ensure(leaked2.Count == 0,
            $"order.total=100 must NOT satisfy a nested {{\"numeric\": [\">\", 100]}} policy: a " +
            $"{F(window2, 0)}s window (>=3x the measured barrier latency of {F(latency2, 1)}s), begun " +
            $"after total=150 had arrived, produced {Show(leaked2)}");
    }

    // ===== helpers =====

    private static string Repr(string? value) => value is null ? "None" : $"'{value}'";

    private static string Show(IEnumerable<string> values) => "[" + string.Join(", ", values.Select(v => $"'{v}'")) + "]";

    private static string F(double value, int digits) => value.ToString("F" + digits.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);

    private static string Marker(Message msg) => (string)JsonNode.Parse(msg.Body)!.AsObject()["marker"]!;

    /// <summary>
    /// Long-poll until every wanted marker has arrived, reading it out of the raw JSON body.
    /// Returns {marker: monotonic arrival time} so the caller can calibrate an absence window from
    /// real measured latency. RAISES on the deadline, and RAISES immediately on a forbidden marker
    /// — a cross-scope match is a failure, not something to wait through. A body that is not JSON,
    /// or that carries no marker, also raises: this check publishes only JSON bodies it built
    /// itself, so anything else is a real defect rather than noise to filter out.
    /// </summary>
    private static async Task<Dictionary<string, double>> AwaitMarkers(FidelityContext ctx, FidelityStopwatch clock, string queueUrl,
        HashSet<string> wanted, double timeoutS, string description, HashSet<string> forbidden)
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
                var marker = Marker(msg);
                if (forbidden.Contains(marker))
                {
                    throw Fail($"{description}: '{marker}' must not have been delivered here");
                }

                if (outstanding.Remove(marker))
                {
                    seen[marker] = clock.Elapsed.TotalSeconds;
                }
            }

            if (outstanding.Count > 0 && deadline.Elapsed >= TimeSpan.FromSeconds(timeoutS))
            {
                throw Fail($"{description}: {Show(outstanding.Order(StringComparer.Ordinal))} never arrived within {timeoutS}s");
            }
        }

        return seen;
    }

    /// <summary>
    /// Alternate long polls across several queues for one shared window and return every marker
    /// each produced. The empty lists are correct defaults: they feed an equality-to-empty
    /// assertion.
    /// </summary>
    private static async Task<Dictionary<string, List<string>>> DrainBoth(FidelityContext ctx, Dictionary<string, string> queues, double windowS)
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
                    output[label].Add(Marker(msg));
                }
            }
        }

        return output;
    }

    private static List<Amazon.SimpleNotificationService.Model.Tag> TagsKv() =>
        [new() { Key = "sim2real", Value = Scenario }, new() { Key = "sim2real:managed", Value = "true" }];

    /// <summary>CreateTopic (tagged + prefix-named) and register DeleteTopic immediately.</summary>
    private static async Task<string> CreateTopic(FidelityContext ctx)
    {
        var sns = ctx.Sns;
        var name = FidelityContext.UniqueName("sim2real-behav-sns-t-");
        var arn = (await sns.CreateTopicAsync(new CreateTopicRequest { Name = name, Attributes = [], Tags = TagsKv() })).TopicArn;
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
            async () => (await sns.GetSubscriptionAttributesAsync(new GetSubscriptionAttributesRequest { SubscriptionArn = subArn })).Attributes?.GetValueOrDefault("PendingConfirmation"),
            pending => pending == "false",
            timeoutS: timeoutS,
            intervalS: 2.0,
            description: $"subscription {subArn} leaves PendingConfirmation");
        return subArn;
    }

    /// <summary>Bounded retry; RAISES on the deadline so a timeout can never read as a pass.</summary>
    private static async Task<string?> PollUntil(Func<Task<string?>> fn, Func<string?, bool> pred, double timeoutS, double intervalS, string description)
    {
        var deadline = FidelityTime.StartNew();
        var value = await fn();
        while (!pred(value))
        {
            if (deadline.Elapsed >= TimeSpan.FromSeconds(timeoutS))
            {
                throw Fail($"{description}: not satisfied within {timeoutS}s, last value {Repr(value)}");
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(intervalS));
            value = await fn();
        }

        return value;
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
