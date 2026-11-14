// Translated from behavioral/sns/check_custom_attribute_type_labels_and_string_array_survive_both_delivery_modes.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Text.Json;
using System.Text.Json.Nodes;
using Amazon.SimpleNotificationService.Model;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;
using SnsMessageAttributeValue = Amazon.SimpleNotificationService.Model.MessageAttributeValue;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sns;

/// <summary>sns.custom-attribute-type-labels-and-string-array-survive-both-delivery-modes</summary>
[Category("AwsEvaluationSuite")]
public sealed class CustomAttributeTypeLabelsAndStringArraySurviveBothDeliveryModes
{
    private const string Scenario = "sns-custom-attribute-type-labels-and-string-";

    /// <summary>
    /// Custom attribute type labels (Number.float, String.json, Binary.gif) and String.Array values,
    /// even a non-JSON String.Array, arrive verbatim in both the envelope and raw SQS delivery.
    /// </summary>
    [Test]
    [DisplayName("sns.custom-attribute-type-labels-and-string-array-survive-both-delivery-modes")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sns = ctx.Sns;
        var topicArn = await CreateTopic(ctx);
        var (envUrl, envArn) = await CreateQueueForTopic(ctx, topicArn);
        var (rawUrl, rawArn) = await CreateQueueForTopic(ctx, topicArn);
        await SubscribeAndConfirm(ctx, topicArn, envArn);
        await SubscribeAndConfirm(ctx, topicArn, rawArn, attributes: new() { ["RawMessageDelivery"] = "true" });

        var marker = FidelityContext.UniqueName("sim2real-behav-sns-labels-");
        byte[] gif = [.. "GIF89a"u8, 0x00, 0xff];
        var published = new Dictionary<string, (string Type, string Value)>
        {
            ["price"] = ("Number.float", "3.14"),
            ["doc"] = ("String.json", "{\"a\":1}"),
            ["tags"] = ("String.Array", "[\"a\",1,true,null]"),
            ["loose"] = ("String.Array", "not-a-json-array"),
        };
        var attrs = published.ToDictionary(kv => kv.Key, kv => new SnsMessageAttributeValue { DataType = kv.Value.Type, StringValue = kv.Value.Value });
        attrs["icon"] = new SnsMessageAttributeValue { DataType = "Binary.gif", BinaryValue = new MemoryStream(gif) };
        var resp = await sns.PublishAsync(new PublishRequest { TopicArn = topicArn, Message = marker, Subject = "labels", MessageAttributes = attrs });
        Ensure(!string.IsNullOrEmpty(resp.MessageId), $"expected a MessageId for the labelled publish, got {resp}");

        var env = (await ReceiveKeyed(ctx, envUrl, EnvelopeMessage, [marker], timeoutS: 90.0,
            description: "the enveloped subscriber receives the labelled publish"))[marker];
        var delivered = JsonNode.Parse(env.Body)!["MessageAttributes"];
        var expectedEnv = new JsonObject();
        foreach (var (k, (t, v)) in published)
        {
            expectedEnv[k] = new JsonObject { ["Type"] = t, ["Value"] = v };
        }

        expectedEnv["icon"] = new JsonObject { ["Type"] = "Binary.gif", ["Value"] = Convert.ToBase64String(gif) };
        Ensure(JsonNode.DeepEquals(delivered, expectedEnv),
            "the envelope must carry every attribute with its declared label and verbatim value; " +
            $"expected {expectedEnv.ToJsonString()}, got {delivered?.ToJsonString()}");

        var raw = (await ReceiveKeyed(ctx, rawUrl, m => m.Body, [marker], timeoutS: 90.0,
            description: "the raw subscriber receives the labelled publish"))[marker];
        var rawAttrs = raw.MessageAttributes ?? [];
        var gotRaw = rawAttrs.Where(kv => kv.Key != "icon").ToDictionary(kv => kv.Key, kv => ((string?)kv.Value.DataType, (string?)kv.Value.StringValue));
        Ensure(gotRaw.Count == published.Count && gotRaw.All(kv => published.TryGetValue(kv.Key, out var p) && p == kv.Value),
            "raw delivery must map each labelled attribute onto an SQS attribute with the same " +
            $"DataType and value, and add nothing (no Subject); expected {Repr(published.ToDictionary(kv => kv.Key, kv => ((string?)kv.Value.Type, (string?)kv.Value.Value)))}, " +
            $"got {Repr(gotRaw)}");
        var icon = rawAttrs.GetValueOrDefault("icon");
        var iconBytes = icon?.BinaryValue?.ToArray() ?? [];
        Ensure(icon?.DataType == "Binary.gif" && iconBytes.SequenceEqual(gif),
            "the Binary.gif attribute must arrive raw as DataType 'Binary.gif' with its bytes; " +
            $"got {(icon is null ? "{}" : $"{{'DataType': '{icon.DataType}', 'StringValue': '{icon.StringValue}', 'BinaryValue': {Convert.ToHexString(iconBytes)}}}")}");
    }

    private static string Repr(Dictionary<string, (string?, string?)> d) =>
        "{" + string.Join(", ", d.Select(kv => $"'{kv.Key}': ('{kv.Value.Item1}', '{kv.Value.Item2}')")) + "}";

    private static string List(IEnumerable<string> items) => "[" + string.Join(", ", items.Select(s => $"'{s}'")) + "]";

    private static string Head(string s, int n) => s.Length <= n ? s : s[..n];

    private static List<Amazon.SimpleNotificationService.Model.Tag> TagsKv() =>
        [new() { Key = "sim2real", Value = Scenario }, new() { Key = "sim2real:managed", Value = "true" }];

    /// <summary>CreateTopic (tagged + prefix-named) and register DeleteTopic immediately.</summary>
    private static async Task<string> CreateTopic(FidelityContext ctx, bool fifo = false)
    {
        var sns = ctx.Sns;
        var name = FidelityContext.UniqueName("sim2real-behav-sns-t-");
        var attrs = new Dictionary<string, string>();
        if (fifo)
        {
            name += ".fifo";
            attrs["FifoTopic"] = "true";
        }

        var arn = (await sns.CreateTopicAsync(new CreateTopicRequest { Name = name, Attributes = attrs, Tags = TagsKv() })).TopicArn;
        ctx.OnCleanup(() => ctx.Sns.DeleteTopicAsync(new DeleteTopicRequest { TopicArn = arn }));
        return arn;
    }

    /// <summary>Create a real SQS queue whose policy lets exactly this topic send to it.</summary>
    private static async Task<(string Url, string Arn)> CreateQueueForTopic(FidelityContext ctx, string topicArn, bool fifo = false)
    {
        var sqs = ctx.Sqs;
        var name = FidelityContext.UniqueName("sim2real-behav-sns-q-");
        var attrs = new Dictionary<string, string>();
        if (fifo)
        {
            name += ".fifo";
            attrs["FifoQueue"] = "true";
        }

        var url = (await sqs.CreateQueueAsync(new CreateQueueRequest
        {
            QueueName = name, Attributes = attrs,
            Tags = new() { ["sim2real"] = Scenario, ["sim2real:managed"] = "true" },
        })).QueueUrl;
        ctx.OnCleanup(() => DeleteQueueIfPresent(ctx, url));
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

    /// <summary>DeleteQueue; a queue the check itself already deleted is the one tolerated outcome.</summary>
    private static async Task DeleteQueueIfPresent(FidelityContext ctx, string url)
    {
        var error = await FidelityContext.CaptureError(() => ctx.Sqs.DeleteQueueAsync(url));
        if (error is not null && error.Code != "AWS.SimpleQueueService.NonExistentQueue")
        {
            throw error.Exception;
        }
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
            timeoutS: timeoutS, intervalS: 1.0,
            description: "the sqs subscription leaves PendingConfirmation");
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
    /// Long-poll until a message keyed <c>key(msg)</c> exists for every wanted key; RAISES on the
    /// deadline. Each message is returned whole (Body plus MessageAttributes).
    /// </summary>
    private static async Task<Dictionary<string, Message>> ReceiveKeyed(FidelityContext ctx, string queueUrl, Func<Message, string?> key,
        HashSet<string> wanted, double timeoutS, string description)
    {
        var sqs = ctx.Sqs;
        var got = new Dictionary<string, Message>();
        var other = new List<string>();
        var deadline = FidelityTime.StartNew();
        while (!wanted.IsSubsetOf(got.Keys))
        {
            var resp = await sqs.ReceiveMessageAsync(new ReceiveMessageRequest
            {
                QueueUrl = queueUrl, MaxNumberOfMessages = 10, WaitTimeSeconds = 5,
                MessageAttributeNames = ["All"], MessageSystemAttributeNames = ["All"],
            });
            foreach (var msg in resp.Messages ?? [])
            {
                await sqs.DeleteMessageAsync(new DeleteMessageRequest { QueueUrl = queueUrl, ReceiptHandle = msg.ReceiptHandle });
                var k = key(msg);
                if (k is not null && wanted.Contains(k) && !got.ContainsKey(k))
                {
                    got[k] = msg;
                }
                else
                {
                    other.Add(Head(msg.Body, 160));
                }
            }

            if (!wanted.IsSubsetOf(got.Keys) && deadline.Elapsed >= TimeSpan.FromSeconds(timeoutS))
            {
                throw Fail($"{description}: {List(wanted.Except(got.Keys).Order(StringComparer.Ordinal))} never arrived within " +
                    $"{timeoutS}s; other bodies seen: {List(other)}");
            }
        }

        return got;
    }

    /// <summary>The Message field of an SNS notification envelope, or None for a non-envelope body.</summary>
    private static string? EnvelopeMessage(Message msg)
    {
        JsonNode? body;
        try
        {
            body = JsonNode.Parse(msg.Body);
        }
        catch (JsonException)
        {
            return null;
        }

        if (body is JsonObject obj && obj["Type"] is JsonValue type && type.TryGetValue<string>(out var t) && t == "Notification")
        {
            return (string?)obj["Message"];
        }

        return null;
    }
}
