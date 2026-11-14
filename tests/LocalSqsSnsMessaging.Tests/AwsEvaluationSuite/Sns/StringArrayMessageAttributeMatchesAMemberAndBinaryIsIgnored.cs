// Translated from behavioral/sns/check_string_array_message_attribute_matches_a_member_and_binary_is_ignored.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Amazon.SimpleNotificationService.Model;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;
using SnsMessageAttributeValue = Amazon.SimpleNotificationService.Model.MessageAttributeValue;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sns;

/// <summary>sns.string-array-message-attribute-matches-a-member-and-binary-is-ignored</summary>
[Category("AwsEvaluationSuite")]
public sealed class StringArrayMessageAttributeMatchesAMemberAndBinaryIsIgnored
{
    private const string Scenario = "sns-attr-types";

    // json.dumps of ["urgent", "billing"] and ["urgent", "shipping"].
    private const string ArrayMatching = """["urgent", "billing"]""";
    private const string ArrayMissing = """["urgent", "shipping"]""";

    /// <summary>
    /// A String.Array attribute matches a filter policy on any member; a Binary one is ignored.
    ///
    /// The Binary attribute is still delivered, and both keep their declared types in the envelope.
    ///
    /// Reference: https://docs.aws.amazon.com/sns/latest/dg/subscription-filter-policy-constraints.html
    /// </summary>
    [Test]
    [DisplayName("sns.string-array-message-attribute-matches-a-member-and-binary-is-ignored")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sns = ctx.Sns;
        var sqs = ctx.Sqs;
        var topicArn = await CreateTopic(ctx);

        var (controlUrl, controlArn) = await CreateQueueForTopic(ctx, topicArn);
        var (filteredUrl, filteredArn) = await CreateQueueForTopic(ctx, topicArn);

        await SubscribeAndConfirm(ctx, topicArn, controlArn);
        var filteredSub = await SubscribeAndConfirm(
            ctx, topicArn, filteredArn,
            attributes: new() { ["FilterPolicy"] = """{"tags": ["billing"]}""" });

        var memberMatch = FidelityContext.UniqueName("sim2real-behav-strarray-member-match-");
        var memberMiss = FidelityContext.UniqueName("sim2real-behav-strarray-member-miss-");
        var binaryIgnored = FidelityContext.UniqueName("sim2real-behav-strarray-binary-ignored-");
        var binaryBytes = Encoding.UTF8.GetBytes(ArrayMatching);

        var started = FidelityTime.UtcNow;
        await sns.PublishAsync(new PublishRequest
        {
            TopicArn = topicArn,
            Message = memberMatch,
            MessageAttributes = new() { ["tags"] = new SnsMessageAttributeValue { DataType = "String.Array", StringValue = ArrayMatching } },
        });
        await sns.PublishAsync(new PublishRequest
        {
            TopicArn = topicArn,
            Message = memberMiss,
            MessageAttributes = new() { ["tags"] = new SnsMessageAttributeValue { DataType = "String.Array", StringValue = ArrayMissing } },
        });
        await sns.PublishAsync(new PublishRequest
        {
            TopicArn = topicArn,
            Message = binaryIgnored,
            MessageAttributes = new() { ["tags"] = new SnsMessageAttributeValue { DataType = "Binary", BinaryValue = new MemoryStream(binaryBytes) } },
        });

        // ---- barrier + representation, both on the unfiltered twin ---------------------------
        var envelopes = await AwaitEnvelopes(
            ctx, controlUrl, [memberMatch, memberMiss, binaryIgnored], timeoutS: 120.0,
            description: "the unfiltered twin must receive all three messages; without this the " +
                         "absences at the filtered arm prove nothing",
            forbidden: []);
        var latency = Math.Max((envelopes.Values.Max(e => e.Seen) - started).TotalSeconds, 1.0);

        var arrayAttr = envelopes[memberMatch].Envelope["MessageAttributes"]!["tags"]!;
        Ensure((string?)arrayAttr["Type"] == "String.Array",
            "expected the delivered envelope to declare Type 'String.Array' for an array " +
            $"attribute, got '{arrayAttr["Type"]}'");
        Ensure(JsonNode.DeepEquals(JsonNode.Parse((string)arrayAttr["Value"]!), JsonNode.Parse(ArrayMatching)),
            $"expected the array attribute Value to parse back to {ArrayMatching} in order, " +
            $"got '{arrayAttr["Value"]}'");

        var binaryAttr = envelopes[binaryIgnored].Envelope["MessageAttributes"]!["tags"]!;
        Ensure((string?)binaryAttr["Type"] == "Binary",
            "expected the Binary attribute to be DELIVERED with Type 'Binary' — the point of the " +
            $"leg below is that it is delivered but not filtered on; got '{binaryAttr["Type"]}'");
        var decoded = Convert.FromBase64String((string)binaryAttr["Value"]!);
        Ensure(decoded.AsSpan().SequenceEqual(binaryBytes),
            $"expected the Binary attribute bytes preserved as b'{ArrayMatching}', got " +
            $"b'{Encoding.UTF8.GetString(decoded)}'");

        // ---- the filtered arm: exactly the member match ---------------------------------------
        await AwaitEnvelopes(
            ctx, filteredUrl, [memberMatch], timeoutS: Math.Max(60.0, 3.0 * latency + 20.0),
            description: "a String.Array attribute must satisfy {\"tags\": [\"billing\"]} when " +
                         "'billing' is one of its members",
            forbidden: [memberMiss, binaryIgnored]);
        var window = Math.Max(3.0 * latency, 15.0);
        var leaked = await Drain(ctx, filteredUrl, windowS: window);
        Ensure(leaked.Count == 0,
            "neither the non-member array nor the Binary attribute may satisfy " +
            $"{{\"tags\": [\"billing\"]}}: a {window.ToString("F0", CultureInfo.InvariantCulture)}s window (>=3x the measured control " +
            $"latency of {latency.ToString("F1", CultureInfo.InvariantCulture)}s), begun after the matching message had already arrived, " +
            $"produced {Repr(leaked)}");
        var approx = int.Parse((await sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest
        {
            QueueUrl = filteredUrl,
            AttributeNames = ["ApproximateNumberOfMessages"],
        })).Attributes!["ApproximateNumberOfMessages"], CultureInfo.InvariantCulture);
        Ensure(approx == 0,
            "second independent observable for the absence: expected ApproximateNumberOfMessages " +
            $"== 0 on the filtered queue, got {approx}");

        // ---- repair exactly one thing: name a member the other array DOES carry ---------------
        await SetFilterPolicy(ctx, filteredSub, JsonNode.Parse("""{"tags": ["shipping"]}""")!);
        await RepublishUntilDelivered(
            ctx, topicArn, filteredUrl, memberMiss,
            new() { ["tags"] = new SnsMessageAttributeValue { DataType = "String.Array", StringValue = ArrayMissing } },
            attempts: 3, perAttemptS: Math.Max(30.0, 2.0 * latency),
            description: "once the policy names 'shipping', the identical array attribute must " +
                         "match on that member and be delivered — this proves member matching " +
                         "rather than the array type suppressing everything");
    }

    private static string Repr(IEnumerable<string?> values) => $"[{string.Join(", ", values.Select(v => v is null ? "None" : $"'{v}'"))}]";

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

    /// <summary>
    /// Long-poll until an envelope arrives for every wanted Message; return {Message: (env, t)}.
    ///
    /// Raises on the deadline, and immediately on a forbidden Message.
    /// </summary>
    private static async Task<Dictionary<string, (JsonObject Envelope, DateTimeOffset Seen)>> AwaitEnvelopes(FidelityContext ctx, string queueUrl,
        HashSet<string> wanted, double timeoutS, string description, HashSet<string> forbidden)
    {
        var sqs = ctx.Sqs;
        var found = new Dictionary<string, (JsonObject, DateTimeOffset)>();
        var outstanding = new HashSet<string>(wanted);
        var others = new List<string>();
        var deadline = FidelityTime.StartNew();
        while (outstanding.Count > 0)
        {
            var resp = await sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = queueUrl, MaxNumberOfMessages = 10, WaitTimeSeconds = 5 });
            foreach (var msg in resp.Messages ?? [])
            {
                await sqs.DeleteMessageAsync(new DeleteMessageRequest { QueueUrl = queueUrl, ReceiptHandle = msg.ReceiptHandle });
                var envelope = JsonNode.Parse(msg.Body)!.AsObject();
                var message = (string?)envelope["Message"];
                if (message is not null && forbidden.Contains(message))
                {
                    throw Fail($"{description}: '{message}' must not have been delivered here");
                }

                if (message is not null && outstanding.Remove(message))
                {
                    found[message] = (envelope, FidelityTime.UtcNow);
                }
                else
                {
                    var text = message ?? "None";
                    others.Add(text.Length <= 60 ? text : text[..60]);
                }
            }

            if (outstanding.Count > 0 && deadline.Elapsed >= TimeSpan.FromSeconds(timeoutS))
            {
                throw Fail($"{description}: {Repr(outstanding.Order(StringComparer.Ordinal))} never arrived within {timeoutS}s; " +
                    $"other Message values seen: {Repr(others)}");
            }
        }

        return found;
    }

    /// <summary>
    /// Republish the same marker up to `attempts` times until it lands; raise if it never does.
    ///
    /// A FilterPolicy update is eventually consistent on the delivery path, hence the retry.
    /// </summary>
    private static async Task RepublishUntilDelivered(FidelityContext ctx, string topicArn, string queueUrl, string marker,
        Dictionary<string, SnsMessageAttributeValue> attributes, int attempts, double perAttemptS, string description)
    {
        var sns = ctx.Sns;
        var sqs = ctx.Sqs;
        var seen = new List<string?>();
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
                    seen.Add((string?)JsonNode.Parse(msg.Body)!["Message"]);
                }

                if (seen.Contains(marker))
                {
                    return;
                }
            }
        }

        throw Fail($"{description}: '{marker}' was never delivered across {attempts} republishes of " +
            $"{perAttemptS.ToString("F0", CultureInfo.InvariantCulture)}s each; Message values seen: {Repr(seen)}");
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

    /// <summary>Long-poll for the whole window and return every body seen.</summary>
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
