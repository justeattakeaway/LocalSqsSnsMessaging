// Translated from behavioral/sns__sqs/check_sns_filter_policy_message_body_scope_suppresses_non_matching.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Text.Json;
using System.Text.Json.Nodes;
using Amazon.SimpleNotificationService.Model;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.SnsSqs;

/// <summary>sns__sqs.sns-filter-policy-message-body-scope-suppresses-non-matching</summary>
[Category("AwsEvaluationSuite")]
public sealed class SnsFilterPolicyMessageBodyScopeSuppressesNonMatching
{
    private const string XsvcPrefix = "sim2real-xsvc-";
    private const string ManagedTagKey = "sim2real:managed";

    /// <summary>
    /// A FilterPolicyScope=MessageBody policy is matched against the JSON body: the matching control
    /// message reaches the queue and the non-matching one, published first, never does.
    /// </summary>
    [Test]
    [DisplayName("sns__sqs.sns-filter-policy-message-body-scope-suppresses-non-matching")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sns = ctx.Sns;
        var topic = await CreateTopic(ctx);
        var q = await CreateQueue(ctx);
        await SetQueuePolicy(ctx, q.Url, AllowServiceSend(q.Arn, "sns.amazonaws.com", topic.Arn));

        var sub = await sns.SubscribeAsync(new SubscribeRequest
        {
            TopicArn = topic.Arn,
            Protocol = "sqs",
            Endpoint = q.Arn,
            Attributes = new()
            {
                ["FilterPolicy"] = PyDumps(new JsonObject { ["kind"] = new JsonArray("wanted") }),
                ["FilterPolicyScope"] = "MessageBody",
                ["RawMessageDelivery"] = "true",
            },
            ReturnSubscriptionArn = true,
        });
        var subArn = sub.SubscriptionArn;
        ctx.OnCleanup(() => ctx.Sns.UnsubscribeAsync(new UnsubscribeRequest { SubscriptionArn = subArn }));

        var scope = (await sns.GetSubscriptionAttributesAsync(new GetSubscriptionAttributesRequest { SubscriptionArn = subArn }))
            .Attributes?.GetValueOrDefault("FilterPolicyScope");
        Ensure(scope == "MessageBody",
            $"FilterPolicyScope did not persist as MessageBody (got '{scope}'); the rest of this check " +
            "would be testing the attribute-scope path instead");

        // Non-matching first, so ordered delivery would put it ahead of the control.
        await sns.PublishAsync(new PublishRequest
        {
            TopicArn = topic.Arn, Message = PyDumps(new JsonObject { ["kind"] = "unwanted", ["marker"] = "suppressed" }),
        });
        await sns.PublishAsync(new PublishRequest
        {
            TopicArn = topic.Arn, Message = PyDumps(new JsonObject { ["kind"] = "wanted", ["marker"] = "control" }),
        });

        var (ctl, others) = await WaitFor(ctx, q.Url,
            b => b is JsonObject o && o["marker"] is JsonValue m && m.GetValueKind() == JsonValueKind.String && m.GetValue<string>() == "control",
            timeoutS: 90.0);
        Ensure(ctl.Count >= 1,
            "control: a body matching {'kind': ['wanted']} must be delivered under MessageBody scope");

        others = [.. others, .. await Collect(ctx, q.Url, windowS: 30.0)];
        var bad = others
            .Select(Body)
            .Where(b => b is JsonObject o && o["marker"] is JsonValue m && m.GetValueKind() == JsonValueKind.String && m.GetValue<string>() == "suppressed")
            .ToList();
        Ensure(bad.Count == 0,
            $"the MessageBody-scoped filter policy delivered {bad.Count} non-matching message(s): [{string.Join(", ", bad.Select(PyRepr))}]");
    }

    private static Dictionary<string, string> XsvcResourceTags() =>
        new() { [ManagedTagKey] = "true", ["sim2real:prefix"] = XsvcPrefix.TrimEnd('-') };

    private static List<Amazon.SimpleNotificationService.Model.Tag> XsvcTagsKv() =>
        [.. XsvcResourceTags().Select(kv => new Amazon.SimpleNotificationService.Model.Tag { Key = kv.Key, Value = kv.Value })];

    /// <summary>A unique, prefix-tagged resource name: sim2real-xsvc-&lt;kind&gt;-&lt;uuid&gt;.</summary>
    private static string XName(string kind) => FidelityContext.UniqueName($"{XsvcPrefix}{kind}-");

    /// <summary>Create an SQS queue (registers delete_queue teardown). Returns {name,url,arn}.</summary>
    private static async Task<(string Name, string Url, string Arn)> CreateQueue(FidelityContext ctx, bool fifo = false, Dictionary<string, string>? attrs = null)
    {
        var baseName = XName("q");
        var name = fifo ? $"{baseName}.fifo" : baseName;
        var sqs = ctx.Sqs;
        var createAttrs = new Dictionary<string, string>(attrs ?? []);
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
    /// This is the NEGATIVE observable. An empty return is only meaningful once a positive control
    /// has already been observed elsewhere in the check -- that is what proves enough wall-clock and
    /// enough propagation elapsed for a delivery to have happened if the emulator were going to make
    /// one. Used alone it would pass against an emulator that delivers nothing at all.
    /// </summary>
    private static async Task<List<Message>> Collect(FidelityContext ctx, string queueUrl, double windowS = 30.0, int maxTotal = 10, bool keep = false)
    {
        var sqs = ctx.Sqs;
        var seen = new List<Message>();
        var clock = FidelityTime.StartNew();
        while (clock.Elapsed < TimeSpan.FromSeconds(windowS) && seen.Count < maxTotal)
        {
            var wait = Math.Min(5, Math.Max(1, (int)(windowS - clock.Elapsed.TotalSeconds)));
            var resp = await sqs.ReceiveMessageAsync(new ReceiveMessageRequest
            {
                QueueUrl = queueUrl, MaxNumberOfMessages = 10, WaitTimeSeconds = wait, MessageAttributeNames = ["All"],
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
    private static async Task<(List<Message> Matched, List<Message> Others)> WaitFor(FidelityContext ctx, string queueUrl,
        Func<JsonNode?, bool> predicate, double timeoutS = 60.0)
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
        ctx.Sqs.SetQueueAttributesAsync(new SetQueueAttributesRequest { QueueUrl = queueUrl, Attributes = new() { ["Policy"] = PyDumps(policy) } });

    /// <summary>Create an SNS topic (registers delete_topic teardown). Returns {name,arn}.</summary>
    private static async Task<(string Name, string Arn)> CreateTopic(FidelityContext ctx)
    {
        var name = XName("topic");
        var sns = ctx.Sns;
        var arn = (await sns.CreateTopicAsync(new CreateTopicRequest { Name = name, Tags = XsvcTagsKv() })).TopicArn;
        ctx.OnCleanup(() => sns.DeleteTopicAsync(new DeleteTopicRequest { TopicArn = arn }));
        return (name, arn);
    }

    /// <summary><c>json.dumps(value)</c> with Python's default separators (", " and ": ").</summary>
    private static string PyDumps(JsonNode? node) => node switch
    {
        null => "null",
        JsonObject o => "{" + string.Join(", ", o.Select(kv => $"{JsonSerializer.Serialize(kv.Key)}: {PyDumps(kv.Value)}")) + "}",
        JsonArray a => "[" + string.Join(", ", a.Select(PyDumps)) + "]",
        _ => node.ToJsonString(),
    };

    /// <summary>Python's <c>repr</c> of a parsed JSON value.</summary>
    private static string PyRepr(JsonNode? node) => node switch
    {
        null => "None",
        JsonObject o => "{" + string.Join(", ", o.Select(kv => $"'{kv.Key}': {PyRepr(kv.Value)}")) + "}",
        JsonArray a => "[" + string.Join(", ", a.Select(PyRepr)) + "]",
        JsonValue v when v.GetValueKind() == JsonValueKind.String => $"'{v.GetValue<string>()}'",
        JsonValue v when v.GetValueKind() == JsonValueKind.True => "True",
        JsonValue v when v.GetValueKind() == JsonValueKind.False => "False",
        _ => node.ToJsonString(),
    };
}
