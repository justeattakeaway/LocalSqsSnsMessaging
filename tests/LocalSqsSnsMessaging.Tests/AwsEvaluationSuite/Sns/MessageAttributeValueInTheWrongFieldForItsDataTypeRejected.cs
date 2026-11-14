// Translated from behavioral/sns/check_message_attribute_value_in_the_wrong_field_for_its_data_type_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Amazon.SimpleNotificationService;
using Amazon.SimpleNotificationService.Model;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;
using SnsMessageAttributeValue = Amazon.SimpleNotificationService.Model.MessageAttributeValue;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sns;

/// <summary>sns.message-attribute-value-in-the-wrong-field-for-its-data-type-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class MessageAttributeValueInTheWrongFieldForItsDataTypeRejected
{
    private const string Scenario = "sns-attribute-value-field";

    /// <summary>
    /// Publish refuses an attribute value in the wrong field for its DataType, or missing, with
    /// ParameterValueInvalid; none is delivered, and correctly-placed values arrive byte-exact.
    /// </summary>
    [Test]
    [DisplayName("sns.message-attribute-value-in-the-wrong-field-for-its-data-type-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    [KnownFidelityGap(FidelityGap.SnsValidation, "Publish accepts a String attribute sent as BinaryValue (and the reverse) instead of refusing it")]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sns = ctx.Sns;
        var topic = await CreateTopic(ctx);
        var (url, queueArn) = await CreateQueueForTopics(ctx, [topic]);
        await SubscribeAndConfirm(ctx, topic, queueArn, new() { ["RawMessageDelivery"] = "true" });
        var runId = FidelityContext.UniqueName("sim2real-behav-sns-attrfield-");

        // (name, value, the value's keys as Python's sorted(value) lists them, expected message)
        var cases = new (string Name, Func<SnsMessageAttributeValue> Value, string[] Keys, string Expected)[]
        {
            ("payload", () => new SnsMessageAttributeValue { DataType = "Binary", StringValue = "aGVsbG8=" }, ["DataType", "StringValue"],
                "The message attribute 'payload' with type 'Binary' must use field 'Binary'."),
            ("label", () => new SnsMessageAttributeValue { DataType = "String", BinaryValue = new MemoryStream("blue"u8.ToArray()) }, ["BinaryValue", "DataType"],
                "The message attribute 'label' with type 'String' must use field 'String'."),
            ("label", () => new SnsMessageAttributeValue { DataType = "String" }, ["DataType"],
                "The message attribute 'label' must contain non-empty message attribute value for " +
                "message attribute type 'String'."),
            ("count", () => new SnsMessageAttributeValue { DataType = "Number" }, ["DataType"],
                "The message attribute 'count' must contain non-empty message attribute value for " +
                "message attribute type 'Number'."),
        };
        var refused = new List<string>();
        for (var i = 0; i < cases.Length; i++)
        {
            var (name, value, keys, expected) = cases[i];
            var body = $"{runId}-refused-{i}";
            refused.Add(body);
            var (code, message) = await Error(() => sns.PublishAsync(new PublishRequest
            {
                TopicArn = topic, Message = body, MessageAttributes = new() { [name] = value() },
            }));
            Ensure((code, message) == ("ParameterValueInvalid", expected),
                $"attribute '{name}' with {List(keys)}: expected ParameterValueInvalid " +
                $"'{expected}', got '{code}' '{message}'");
        }

        var good = $"{runId}-accepted";
        await sns.PublishAsync(new PublishRequest
        {
            TopicArn = topic, Message = good, MessageAttributes = new()
            {
                ["payload"] = new SnsMessageAttributeValue { DataType = "Binary", BinaryValue = new MemoryStream([0x00, 0xff, .. "hello"u8]) },
                ["label"] = new SnsMessageAttributeValue { DataType = "String", StringValue = "blue" },
                ["count"] = new SnsMessageAttributeValue { DataType = "Number", StringValue = "3" },
            },
        });
        var seen = await DrainUntil(ctx, url, [good]);
        // {k: (DataType, BinaryValue or StringValue)}, with each value in its Python repr.
        var attrs = (seen[good].MessageAttributes ?? []).ToDictionary(
            kv => kv.Key,
            kv => (kv.Value.DataType, kv.Value.BinaryValue is { Length: > 0 } bin ? PyBytes(bin.ToArray()) : PyStr(kv.Value.StringValue)));
        var want = new Dictionary<string, (string?, string)>
        {
            ["payload"] = ("Binary", PyBytes([0x00, 0xff, .. "hello"u8])),
            ["label"] = ("String", PyStr("blue")),
            ["count"] = ("Number", PyStr("3")),
        };
        Ensure(attrs.Count == want.Count && attrs.All(kv => want.TryGetValue(kv.Key, out var w) && w == kv.Value),
            "the well-formed attributes must arrive as SQS attributes byte-exact, got " +
            "{" + string.Join(", ", attrs.Select(kv => $"'{kv.Key}': ('{kv.Value.DataType}', {kv.Value.Item2})")) + "}");
        var leaked = refused.Intersect(seen.Keys).Order(StringComparer.Ordinal).ToList();
        Ensure(leaked.Count == 0, $"refused publishes were delivered anyway: {List(leaked)}");
        Ensure(seen.Count == 1 && seen.ContainsKey(good), $"the queue received unexpected messages: {List(seen.Keys.Order(StringComparer.Ordinal))}");
    }

    private static string List(IEnumerable<string> items) => "[" + string.Join(", ", items.Select(s => $"'{s}'")) + "]";

    private static string PyStr(string? s) => s is null ? "None" : $"'{s}'";

    /// <summary>Python's repr of a bytes value, e.g. <c>b'\x00\xffhello'</c>.</summary>
    private static string PyBytes(byte[] bytes)
    {
        var sb = new StringBuilder("b'");
        foreach (var b in bytes)
        {
            sb.Append(b switch
            {
                (byte)'\\' => "\\\\",
                (byte)'\'' => "\\'",
                (byte)'\t' => "\\t",
                (byte)'\n' => "\\n",
                (byte)'\r' => "\\r",
                >= 0x20 and < 0x7f => ((char)b).ToString(),
                _ => $"\\x{b:x2}",
            });
        }

        return sb.Append('\'').ToString();
    }

    /// <summary>The (Code, Message) of the ClientError <c>fn</c> raises. An unexpected success raises too.</summary>
    private static async Task<(string? Code, string? Message)> Error<T>(Func<Task<T>> fn)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is null)
        {
            throw Fail($"expected the call to be refused, but it succeeded: {result}");
        }

        return (error.Code, error.Message ?? "");
    }

    private static List<Amazon.SimpleNotificationService.Model.Tag> TagsKv() =>
        [new() { Key = "sim2real", Value = Scenario }, new() { Key = "sim2real:managed", Value = "true" }];

    /// <summary>CreateTopic (tagged + prefix-named) and register DeleteTopic immediately.</summary>
    private static async Task<string> CreateTopic(FidelityContext ctx, string prefix = "sim2real-behav-sns-t-")
    {
        var sns = ctx.Sns;
        var arn = (await sns.CreateTopicAsync(new CreateTopicRequest { Name = FidelityContext.UniqueName(prefix), Tags = TagsKv() })).TopicArn;
        ctx.OnCleanup(() => sns.DeleteTopicAsync(new DeleteTopicRequest { TopicArn = arn }));
        return arn;
    }

    /// <summary>Create a real SQS queue whose policy lets exactly these topics send to it.</summary>
    private static async Task<(string Url, string Arn)> CreateQueueForTopics(FidelityContext ctx, List<string> topicArns)
    {
        var sqs = ctx.Sqs;
        var url = (await sqs.CreateQueueAsync(new CreateQueueRequest
        {
            QueueName = FidelityContext.UniqueName("sim2real-behav-sns-q-"),
            Tags = new() { ["sim2real"] = Scenario, ["sim2real:managed"] = "true" },
        })).QueueUrl;
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(url));
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
                ["Condition"] = new JsonObject
                {
                    ["ArnEquals"] = new JsonObject { ["aws:SourceArn"] = new JsonArray([.. topicArns.Select(t => JsonValue.Create(t))]) },
                },
            }),
        };
        await sqs.SetQueueAttributesAsync(new SetQueueAttributesRequest { QueueUrl = url, Attributes = new() { ["Policy"] = policy.ToJsonString() } });
        return (url, arn);
    }

    /// <summary>Subscribe an SQS queue, register Unsubscribe, poll until PendingConfirmation is false.</summary>
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
        ctx.OnCleanup(() => UnsubscribeIfPresent(sns, subArn));
        var deadline = FidelityTime.StartNew();
        string? pending = null;
        while (deadline.Elapsed < TimeSpan.FromSeconds(timeoutS))
        {
            pending = (await sns.GetSubscriptionAttributesAsync(new GetSubscriptionAttributesRequest { SubscriptionArn = subArn }))
                .Attributes?.GetValueOrDefault("PendingConfirmation");
            if (pending == "false")
            {
                return subArn;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(1.0));
        }

        throw Fail($"the sqs subscription was still PendingConfirmation='{pending}' after {timeoutS}s");
    }

    /// <summary>Unsubscribe; a subscription already removed with its topic is the tolerated outcome.</summary>
    private static async Task UnsubscribeIfPresent(AmazonSimpleNotificationServiceClient sns, string subArn)
    {
        var error = await FidelityContext.CaptureError(() => sns.UnsubscribeAsync(new UnsubscribeRequest { SubscriptionArn = subArn }));
        if (error is not null && error.Code != "NotFound")
        {
            throw error.Exception;
        }
    }

    /// <summary>The published text of a delivered SQS message: the envelope's Message, else the raw body.</summary>
    private static string Key(Message msg)
    {
        var body = msg.Body ?? "";
        JsonNode? env;
        try
        {
            env = JsonNode.Parse(body);
        }
        catch (JsonException)
        {
            return body;
        }

        if (env is JsonObject obj && obj["Type"] is JsonValue type && type.TryGetValue<string>(out var t) && t == "Notification")
        {
            return (string?)obj["Message"] ?? "";
        }

        return body;
    }

    /// <summary>
    /// Receive (and delete) until every key in <c>wanted</c> has been seen; return key -> message
    /// for EVERY message seen on the way, so a caller can assert what else arrived.
    /// </summary>
    private static async Task<Dictionary<string, Message>> DrainUntil(FidelityContext ctx, string url, HashSet<string> wanted, double timeoutS = 90.0)
    {
        var sqs = ctx.Sqs;
        var seen = new Dictionary<string, Message>();
        var deadline = FidelityTime.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(timeoutS) && !wanted.IsSubsetOf(seen.Keys))
        {
            var resp = await sqs.ReceiveMessageAsync(new ReceiveMessageRequest
            {
                QueueUrl = url, MaxNumberOfMessages = 10, WaitTimeSeconds = 5,
                MessageAttributeNames = ["All"], MessageSystemAttributeNames = ["All"],
            });
            foreach (var msg in resp.Messages ?? [])
            {
                await sqs.DeleteMessageAsync(new DeleteMessageRequest { QueueUrl = url, ReceiptHandle = msg.ReceiptHandle });
                seen[Key(msg)] = msg;
            }
        }

        var missing = wanted.Except(seen.Keys).Order(StringComparer.Ordinal).ToList();
        Ensure(missing.Count == 0,
            $"within {timeoutS}s the queue never received {List(missing)}; it received {List(seen.Keys.Order(StringComparer.Ordinal))}");
        return seen;
    }
}
