// Translated from behavioral/sns__sqs/check_sns_sqs_queue_policy_deny_suppresses_delivery.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Text.Json;
using System.Text.Json.Nodes;
using Amazon.SimpleNotificationService.Model;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.SnsSqs;

/// <summary>sns__sqs.sns-sqs-queue-policy-deny-suppresses-delivery</summary>
[Category("AwsEvaluationSuite")]
public sealed class SnsSqsQueuePolicyDenySuppressesDelivery
{
    private const string XsvcPrefix = "sim2real-xsvc-";
    private const string ManagedTagKey = "sim2real:managed";

    /// <summary>
    /// An explicit Deny added to a queue policy that still Allows the topic stops SNS delivery while
    /// Publish still returns a MessageId; before the Deny, the same topic, subscription and queue
    /// deliver a control message.
    /// </summary>
    [Test]
    [DisplayName("sns__sqs.sns-sqs-queue-policy-deny-suppresses-delivery")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    [KnownFidelityGap(FidelityGap.SnsDeliveryFailures, "An explicit Deny of sqs:SendMessage to sns.amazonaws.com in the queue policy does not stop SNS delivery")]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sns = ctx.Sns;
        var sqs = ctx.Sqs;
        var topic = await CreateTopic(ctx);
        var q = await CreateQueue(ctx);

        var allow = AllowServiceSend(q.Arn, "sns.amazonaws.com", topic.Arn);
        await SetQueuePolicy(ctx, q.Url, allow);

        var sub = await sns.SubscribeAsync(new SubscribeRequest
        {
            TopicArn = topic.Arn,
            Protocol = "sqs",
            Endpoint = q.Arn,
            Attributes = new() { ["RawMessageDelivery"] = "true" },
            ReturnSubscriptionArn = true,
        });
        var subArn = sub.SubscriptionArn;
        ctx.OnCleanup(() => ctx.Sns.UnsubscribeAsync(new UnsubscribeRequest { SubscriptionArn = subArn }));

        // Control: the whole SNS -> SQS path works under the Allow-only policy.
        await sns.PublishAsync(new PublishRequest { TopicArn = topic.Arn, Message = new JsonObject { ["marker"] = "control" }.ToJsonString() });
        var (ctl, _) = await WaitFor(
            ctx,
            q.Url,
            b => b is JsonObject o && o["marker"] is JsonValue v && v.TryGetValue<string>(out var s) && s == "control",
            timeoutS: 90.0);
        Ensure(ctl.Count >= 1,
            "control: SNS must deliver to the queue while the policy only Allows -- without this the " +
            "suppression below would be indistinguishable from broken wiring");

        // Change exactly one thing: add an explicit Deny alongside the still-present Allow.
        const string denySid = "DenyServiceSend";
        var denied = allow.DeepClone().AsObject();
        denied["Statement"]!.AsArray().Add(new JsonObject
        {
            ["Sid"] = denySid,
            ["Effect"] = "Deny",
            ["Principal"] = new JsonObject { ["Service"] = "sns.amazonaws.com" },
            ["Action"] = "sqs:SendMessage",
            ["Resource"] = q.Arn,
        });
        await SetQueuePolicy(ctx, q.Url, denied);
        await PollUntil(
            async () => (await sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest { QueueUrl = q.Url, AttributeNames = ["Policy"] }))
                .Attributes?.GetValueOrDefault("Policy") ?? "",
            p => p.Contains(denySid, StringComparison.Ordinal),
            timeoutS: 60.0,
            intervalS: 3.0,
            description: "queue policy reflects the explicit Deny");

        var resp = await sns.PublishAsync(new PublishRequest { TopicArn = topic.Arn, Message = new JsonObject { ["marker"] = "suppressed" }.ToJsonString() });
        Ensure(!string.IsNullOrEmpty(resp.MessageId),
            $"Publish must still succeed and mint a MessageId even though the destination denies " +
            $"delivery; got {resp}");

        var leaked = await Collect(ctx, q.Url, windowS: 45.0);
        var bad = leaked
            .Select(Body)
            .Where(b => b is JsonObject o && o["marker"] is JsonValue v && v.TryGetValue<string>(out var s) && s == "suppressed")
            .ToList();
        Ensure(bad.Count == 0,
            $"an explicit Deny on sqs:SendMessage did not stop SNS delivery -- {bad.Count} message(s) " +
            $"arrived: [{string.Join(", ", bad.Select(PyRepr))}]");
    }

    /// <summary>Python's repr of a parsed JSON value, enough for the bodies this check publishes.</summary>
    private static string PyRepr(JsonNode? node) => node switch
    {
        null => "None",
        JsonObject o => "{" + string.Join(", ", o.Select(kv => $"'{kv.Key}': {PyRepr(kv.Value)}")) + "}",
        JsonArray a => "[" + string.Join(", ", a.Select(PyRepr)) + "]",
        JsonValue v when v.TryGetValue<string>(out var s) => $"'{s}'",
        _ => node.ToJsonString(),
    };

    private static Dictionary<string, string> XsvcResourceTags() =>
        new() { [ManagedTagKey] = "true", ["sim2real:prefix"] = XsvcPrefix.TrimEnd('-') };

    private static List<Amazon.SimpleNotificationService.Model.Tag> XsvcTagsKv() =>
        XsvcResourceTags().Select(kv => new Amazon.SimpleNotificationService.Model.Tag { Key = kv.Key, Value = kv.Value }).ToList();

    /// <summary>A unique, prefix-tagged resource name: sim2real-xsvc-&lt;kind&gt;-&lt;uuid&gt;.</summary>
    private static string XName(string kind) => FidelityContext.UniqueName($"{XsvcPrefix}{kind}-");

    /// <summary>Poll probe() until predicate(result) is truthy; return the final result (raises on timeout).</summary>
    private static async Task<string> PollUntil(Func<Task<string>> probe, Func<string, bool> predicate,
        double timeoutS = 60.0, double intervalS = 1.0, string description = "condition")
    {
        var deadline = FidelityTime.StartNew();
        string? last = null;
        while (deadline.Elapsed < TimeSpan.FromSeconds(timeoutS))
        {
            last = await probe();
            if (predicate(last))
            {
                return last;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(intervalS));
        }

        throw Fail($"{description} not met within {timeoutS}s. Last result: {(last is null ? "None" : $"'{last}'")}");
    }

    /// <summary>Create an SQS queue (registers delete_queue teardown). Returns {name,url,arn}.</summary>
    private static async Task<(string Name, string Url, string Arn)> CreateQueue(FidelityContext ctx, bool fifo = false)
    {
        var baseName = XName("q");
        var name = fifo ? $"{baseName}.fifo" : baseName;
        var sqs = ctx.Sqs;
        var createAttrs = new Dictionary<string, string>();
        if (fifo)
        {
            createAttrs["FifoQueue"] = "true";
        }

        var resp = await sqs.CreateQueueAsync(new CreateQueueRequest { QueueName = name, Attributes = createAttrs, Tags = XsvcResourceTags() });
        var url = resp.QueueUrl;
        var arn = (await sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest { QueueUrl = url, AttributeNames = ["QueueArn"] })).Attributes!["QueueArn"];
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(url));
        return (name, url, arn);
    }

    /// <summary>Parse an SQS message body as JSON, falling back to the raw string.</summary>
    private static JsonNode? Body(Message msg)
    {
        var raw = msg.Body ?? "";
        try
        {
            return JsonNode.Parse(raw);
        }
        catch (JsonException)
        {
            return JsonValue.Create(raw);
        }
    }

    /// <summary>
    /// Drain a queue over a bounded window with long-polling ReceiveMessage(MaxNumberOfMessages=10)
    /// and return EVERY message seen. Never raises and never swallows an assertion.
    /// </summary>
    /// <remarks>
    /// This is the NEGATIVE observable. An empty return is only meaningful once a positive control
    /// has already been observed elsewhere in the check -- that is what proves enough wall-clock and
    /// enough propagation elapsed for a delivery to have happened if the emulator were going to make
    /// one. Used alone it would pass against an emulator that delivers nothing at all.
    /// </remarks>
    private static async Task<List<Message>> Collect(FidelityContext ctx, string queueUrl, double windowS = 30.0, int maxTotal = 10, bool keep = false)
    {
        var sqs = ctx.Sqs;
        var seen = new List<Message>();
        var deadline = FidelityTime.StartNew();
        var window = TimeSpan.FromSeconds(windowS);
        while (deadline.Elapsed < window && seen.Count < maxTotal)
        {
            var wait = Math.Min(5, Math.Max(1, (int)(window - deadline.Elapsed).TotalSeconds));
            var resp = await sqs.ReceiveMessageAsync(new ReceiveMessageRequest
            {
                QueueUrl = queueUrl,
                MaxNumberOfMessages = 10,
                WaitTimeSeconds = wait,
                MessageAttributeNames = ["All"],
            });
            foreach (var msg in resp.Messages ?? [])
            {
                seen.Add(msg);
                if (!keep)
                {
                    await sqs.DeleteMessageAsync(new DeleteMessageRequest { QueueUrl = queueUrl, ReceiptHandle = msg.ReceiptHandle });
                }
            }
        }

        return seen;
    }

    /// <summary>
    /// Poll a queue until a message whose parsed body satisfies predicate() arrives.
    /// Returns (matched, others) -- every non-matching message seen along the way is returned too so
    /// the caller can assert on leakage rather than silently discarding it.
    /// </summary>
    private static async Task<(List<Message> Matched, List<Message> Others)> WaitFor(FidelityContext ctx, string queueUrl, Func<JsonNode?, bool> predicate, double timeoutS = 60.0)
    {
        var matched = new List<Message>();
        var others = new List<Message>();
        var deadline = FidelityTime.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(timeoutS) && matched.Count == 0)
        {
            foreach (var msg in await Collect(ctx, queueUrl, windowS: Math.Min(8.0, timeoutS)))
            {
                (predicate(Body(msg)) ? matched : others).Add(msg);
            }
        }

        return (matched, others);
    }

    /// <summary>A queue policy granting one AWS service SendMessage on this queue, scoped by SourceArn.</summary>
    private static JsonObject AllowServiceSend(string queueArn, string service, string sourceArn) => new()
    {
        ["Version"] = "2012-10-17",
        ["Statement"] = new JsonArray(new JsonObject
        {
            ["Sid"] = "AllowServiceSend",
            ["Effect"] = "Allow",
            ["Principal"] = new JsonObject { ["Service"] = service },
            ["Action"] = "sqs:SendMessage",
            ["Resource"] = queueArn,
            ["Condition"] = new JsonObject { ["ArnEquals"] = new JsonObject { ["aws:SourceArn"] = sourceArn } },
        }),
    };

    private static Task SetQueuePolicy(FidelityContext ctx, string queueUrl, JsonObject policy) =>
        ctx.Sqs.SetQueueAttributesAsync(new SetQueueAttributesRequest { QueueUrl = queueUrl, Attributes = new() { ["Policy"] = policy.ToJsonString() } });

    /// <summary>Create an SNS topic (registers delete_topic teardown). Returns {name,arn}.</summary>
    private static async Task<(string Name, string Arn)> CreateTopic(FidelityContext ctx)
    {
        var name = XName("topic");
        var sns = ctx.Sns;
        var arn = (await sns.CreateTopicAsync(new CreateTopicRequest { Name = name, Tags = XsvcTagsKv() })).TopicArn;
        ctx.OnCleanup(() => sns.DeleteTopicAsync(new DeleteTopicRequest { TopicArn = arn }));
        return (name, arn);
    }
}
