// Translated from behavioral/sns__sqs/check_sns_to_sqs_envelope_carries_the_subject_and_typed_message_attributes.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Amazon.SimpleNotificationService.Model;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;
using SqsMessageAttributeValue = Amazon.SQS.Model.MessageAttributeValue;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.SnsSqs;

/// <summary>sns__sqs.sns-to-sqs-envelope-carries-the-subject-and-typed-message-attributes</summary>
[Category("AwsEvaluationSuite")]
public sealed partial class SnsToSqsEnvelopeCarriesTheSubjectAndTypedMessageAttributes
{
    private const string XsvcPrefix = "sim2real-xsvc-";
    private const string ManagedTagKey = "sim2real:managed";

    /// <summary>
    /// Bounded convergence for a delete to reach its terminal not-found answer (the default service
    /// class budget); a deadline, never an assertion on elapsed time.
    /// </summary>
    private const double GoneS = 40.0;

    private static readonly string[] SqsNf = ["AWS.SimpleQueueService.NonExistentQueue", "QueueDoesNotExist"];
    private const string SqsNfMsg = "The specified queue does not exist.";

    [GeneratedRegex(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{3}Z$")]
    private static partial Regex Timestamp();

    /// <summary>
    /// A Publish with a Subject, a multibyte JSON-looking message and String, Number and Binary
    /// attributes reaches the subscribed queue as one envelope carrying all of them exactly, with the
    /// Publish MessageId, SignatureVersion 1, a millisecond timestamp and this subscription's unsubscribe URL.
    /// </summary>
    [Test]
    [DisplayName("sns__sqs.sns-to-sqs-envelope-carries-the-subject-and-typed-message-attributes")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    [KnownFidelityGap(FidelityGap.SnsEnvelope, "non-raw delivery also sets a 'TopicArn' SQS message attribute on the delivered message, where AWS sets none")]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var topic = await CreateTopic(ctx);
        var queue = await CreateQueue(ctx);
        await AllowTopic(ctx, queue, topic.Arn);
        var sns = ctx.Sns;
        var subArn = (await sns.SubscribeAsync(new SubscribeRequest
        {
            TopicArn = topic.Arn, Protocol = "sqs", Endpoint = queue.Arn, ReturnSubscriptionArn = true,
        })).SubscriptionArn;
        ctx.OnCleanup(Tolerant(() => ctx.Sns.UnsubscribeAsync(new UnsubscribeRequest { SubscriptionArn = subArn }), "NotFound"));

        var marker = FidelityContext.UniqueName("marker-");
        // json.dumps(..., ensure_ascii=False)
        var message = $"{{\"marker\": \"{marker}\", \"text\": \"Grüße 中文 \U0001f4e6\"}}";
        const string subject = "Order shipped: #42 (ünïcode)";
        var blob = Enumerable.Range(0, 256).Where(i => i % 17 == 0).Select(i => (byte)i).ToArray();
        var published = await sns.PublishAsync(new PublishRequest
        {
            TopicArn = topic.Arn,
            Message = message,
            Subject = subject,
            MessageAttributes = new()
            {
                ["tenant"] = new() { DataType = "String", StringValue = "Example Corp" },
                ["qty"] = new() { DataType = "Number", StringValue = "7.25" },
                ["blob"] = new() { DataType = "Binary", BinaryValue = new MemoryStream(blob) },
            },
        });
        var messages = await WaitForMessages(ctx, queue.Url, expectedCount: 1, timeoutS: 40.0);
        // Delivery is at least once: a repeat of this Publish's envelope is allowed, any other message
        // is a stray.
        var later = (await ctx.Sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = queue.Url, MaxNumberOfMessages = 10, WaitTimeSeconds = 5 })).Messages ?? [];
        var strays = later.Select(m => m.Body).Where(b => EnvelopeId(b) != published.MessageId).ToList();
        Ensure(strays.Count == 0, $"one Publish also delivered other messages to the queue: {Head($"[{string.Join(", ", strays.Select(s => $"'{s}'"))}]", 300)}");
        var envelope = messages[0].Body as JsonObject;
        Ensure(envelope is not null,
            $"expected an SNS envelope object, got {PyTypeName(messages[0].Body)}");
        var want = new JsonObject
        {
            ["Type"] = "Notification",
            ["MessageId"] = published.MessageId,
            ["TopicArn"] = topic.Arn,
            ["Subject"] = subject,
            ["Message"] = message,
            ["SignatureVersion"] = "1",
        };
        var got = new JsonObject();
        foreach (var (k, _) in want)
        {
            got[k] = envelope[k]?.DeepClone();
        }

        Ensure(JsonNode.DeepEquals(got, want), $"envelope fields {PyRepr(got)}, expected {PyRepr(want)}");
        var wantAttrs = new JsonObject
        {
            ["tenant"] = new JsonObject { ["Type"] = "String", ["Value"] = "Example Corp" },
            ["qty"] = new JsonObject { ["Type"] = "Number", ["Value"] = "7.25" },
            ["blob"] = new JsonObject { ["Type"] = "Binary", ["Value"] = Convert.ToBase64String(blob) },
        };
        Ensure(JsonNode.DeepEquals(envelope["MessageAttributes"], wantAttrs),
            $"envelope MessageAttributes {PyRepr(envelope["MessageAttributes"])}, expected {PyRepr(wantAttrs)}");
        var timestamp = envelope["Timestamp"] is JsonValue ts && ts.GetValueKind() == JsonValueKind.String ? ts.GetValue<string>() : "";
        Ensure(Timestamp().IsMatch(timestamp),
            $"envelope Timestamp {PyRepr(envelope["Timestamp"])} is not ISO 8601 UTC with milliseconds");
        var unsubscribe = envelope["UnsubscribeURL"] is JsonValue us && us.GetValueKind() == JsonValueKind.String ? us.GetValue<string>() : "";
        Ensure(unsubscribe.EndsWith($"/?Action=Unsubscribe&SubscriptionArn={subArn}", StringComparison.Ordinal),
            "envelope UnsubscribeURL does not end with this subscription's Unsubscribe action");
        // The message attributes ride inside the envelope body, not as SQS message attributes.
        Ensure(messages[0].Attributes.Count == 0,
            $"non-raw delivery set SQS message attributes [{string.Join(", ", messages[0].Attributes.Keys.Order(StringComparer.Ordinal).Select(k => $"'{k}'"))}], expected none");
        await GoneSubscription(ctx, subArn, topic.Arn);
        await GoneTopic(ctx, topic.Arn);
        await GoneQueue(ctx, queue.Name, queue.Url);
    }

    private static string Head(string s, int n) => s.Length <= n ? s : s[..n];

    /// <summary><c>type(value).__name__</c> of a parsed JSON value.</summary>
    private static string PyTypeName(JsonNode? node) => node switch
    {
        null => "NoneType",
        JsonObject => "dict",
        JsonArray => "list",
        _ => node.GetValueKind() switch
        {
            JsonValueKind.String => "str",
            JsonValueKind.True or JsonValueKind.False => "bool",
            _ => "float",
        },
    };

    private static async Task AllowTopic(FidelityContext ctx, (string Name, string Url, string Arn) queue, string topicArn)
    {
        var policy = new JsonObject
        {
            ["Version"] = "2012-10-17",
            ["Statement"] = new JsonArray(new JsonObject
            {
                ["Effect"] = "Allow",
                ["Principal"] = new JsonObject { ["Service"] = "sns.amazonaws.com" },
                ["Action"] = "sqs:SendMessage",
                ["Resource"] = queue.Arn,
                ["Condition"] = new JsonObject { ["ArnEquals"] = new JsonObject { ["aws:SourceArn"] = topicArn } },
            }),
        };
        await ctx.Sqs.SetQueueAttributesAsync(new SetQueueAttributesRequest { QueueUrl = queue.Url, Attributes = new() { ["Policy"] = policy.ToJsonString() } });
    }

    /// <summary>The SNS envelope MessageId of a raw SQS body, or None for a body that is no envelope.</summary>
    private static string? EnvelopeId(string raw)
    {
        JsonNode? body;
        try
        {
            body = JsonNode.Parse(raw);
        }
        catch (JsonException)
        {
            return null;
        }

        return body is JsonObject o && o["MessageId"] is JsonValue v && v.GetValueKind() == JsonValueKind.String ? v.GetValue<string>() : null;
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
        ctx.OnCleanup(Tolerant(() => sqs.DeleteQueueAsync(url), SqsNf));
        return (name, url, arn);
    }

    /// <summary>Create an SNS topic (registers delete_topic teardown). Returns {name,arn}.</summary>
    private static async Task<(string Name, string Arn)> CreateTopic(FidelityContext ctx)
    {
        var name = XName("topic");
        var sns = ctx.Sns;
        var arn = (await sns.CreateTopicAsync(new CreateTopicRequest { Name = name, Tags = XsvcTagsKv() })).TopicArn;
        ctx.OnCleanup(Tolerant(() => sns.DeleteTopicAsync(new DeleteTopicRequest { TopicArn = arn }), "NotFound"));
        return (name, arn);
    }

    private sealed record Received(JsonNode? Body, string MessageId, string ReceiptHandle, Dictionary<string, SqsMessageAttributeValue> Attributes);

    /// <summary>Poll SQS until expected_count messages arrive; return them (raises on timeout).</summary>
    private static async Task<List<Received>> WaitForMessages(FidelityContext ctx, string queueUrl, int expectedCount = 1, double timeoutS = 40.0, bool delete = true)
    {
        var sqs = ctx.Sqs;
        var messages = new List<Received>();
        var clock = FidelityTime.StartNew();
        while (messages.Count < expectedCount && clock.Elapsed < TimeSpan.FromSeconds(timeoutS))
        {
            var remaining = Math.Max(1, (int)(timeoutS - clock.Elapsed.TotalSeconds));
            var resp = await sqs.ReceiveMessageAsync(new ReceiveMessageRequest
            {
                QueueUrl = queueUrl,
                MaxNumberOfMessages = Math.Min(10, expectedCount - messages.Count),
                WaitTimeSeconds = Math.Min(5, remaining),
                MessageAttributeNames = ["All"],
            });
            foreach (var msg in resp.Messages ?? [])
            {
                JsonNode? body;
                try
                {
                    body = JsonNode.Parse(msg.Body);
                }
                catch (JsonException)
                {
                    body = JsonValue.Create(msg.Body);
                }

                messages.Add(new Received(body, msg.MessageId, msg.ReceiptHandle, msg.MessageAttributes ?? []));
                if (delete)
                {
                    await sqs.DeleteMessageAsync(new DeleteMessageRequest { QueueUrl = queueUrl, ReceiptHandle = msg.ReceiptHandle });
                }
            }
        }

        if (messages.Count < expectedCount)
        {
            throw Fail($"Expected {expectedCount} messages, got {messages.Count} within {timeoutS}s");
        }

        return messages;
    }

    /// <summary>(code, HTTP status, message) of a ClientError.</summary>
    private static (string Code, int Status, string Message) Err(AwsError e) => (e.Code ?? "", (int)e.StatusCode, e.Message ?? "");

    /// <summary><c>fn()</c> must fail with one of <c>codes</c> in the HTTP class of <c>status</c>, with exactly <c>message</c>.</summary>
    private static async Task Refused(Func<Task> fn, string[] codes, int status, string what, string message)
    {
        var error = await FidelityContext.CaptureError(fn);
        if (error is not null)
        {
            var (code, got, msg) = Err(error);
            var inClass = got / 100 == status / 100;
            Ensure(codes.Contains(code) && inClass,
                $"{what}: expected {string.Join("/", codes)} HTTP {status / 100}xx, got '{code}' HTTP {got}: '{Head(msg, 150)}'");
            Ensure(msg == message, $"{what}: {code} message is '{Head(msg, 200)}', expected '{message}'");
            return;
        }

        throw Fail($"{what}: expected {string.Join("/", codes)} HTTP {status}, the call succeeded");
    }

    /// <summary>
    /// Poll <c>fn</c> until it reaches the terminal not-found answer, then grade it with <c>_refused</c>.
    /// A success while the delete converges is retried; any other error fails at once.
    /// </summary>
    private static async Task PollRefused<T>(Func<Task<T>> fn, string[] codes, int status, string what, string message, double timeoutS = GoneS)
        where T : class
    {
        var deadline = FidelityTime.StartNew();
        while (true)
        {
            var (last, error) = await FidelityContext.Capture(fn);
            if (error is not null)
            {
                var (code, _, _) = Err(error);
                if (codes.Contains(code))
                {
                    await Refused(fn, codes, status, what, message);
                    return;
                }

                throw error.Exception;
            }

            if (deadline.Elapsed >= TimeSpan.FromSeconds(timeoutS))
            {
                throw Fail($"{what}: still readable {timeoutS:F0}s after the delete; last answer {Head($"{last}", 300)}");
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(1.0));
        }
    }

    /// <summary>A teardown that ignores only the given not-found codes (the body already deleted it).</summary>
    private static Func<Task> Tolerant(Func<Task> fn, params string[] codes) => async () =>
    {
        var error = await FidelityContext.CaptureError(fn);
        if (error is not null && !codes.Contains(Err(error).Code))
        {
            throw error.Exception;
        }
    };

    private static async Task GoneQueue(FidelityContext ctx, string name, string url)
    {
        var sqs = ctx.Sqs;
        await sqs.DeleteQueueAsync(new DeleteQueueRequest { QueueUrl = url });
        // A deleted queue can keep answering for a while, so the read is polled.
        await PollRefused(() => sqs.GetQueueUrlAsync(new GetQueueUrlRequest { QueueName = name }), SqsNf, 400, $"GetQueueUrl {name}", SqsNfMsg);
        var listed = (await sqs.ListQueuesAsync(new ListQueuesRequest { QueueNamePrefix = name })).QueueUrls ?? [];
        Ensure(!listed.Any(u => u.TrimEnd('/').Split('/')[^1] == name), $"ListQueues still lists deleted queue {name}");
        await Refused(() => sqs.SendMessageAsync(new SendMessageRequest { QueueUrl = url, MessageBody = "gone" }), SqsNf, 400, $"SendMessage {name}", SqsNfMsg);
    }

    private static async Task GoneTopic(FidelityContext ctx, string arn)
    {
        var sns = ctx.Sns;
        await sns.DeleteTopicAsync(new DeleteTopicRequest { TopicArn = arn });
        string[] nf = ["NotFound"];
        await PollRefused(() => sns.GetTopicAttributesAsync(new GetTopicAttributesRequest { TopicArn = arn }), nf, 404, "GetTopicAttributes", "Topic does not exist");
        var listed = new HashSet<string>();
        await foreach (var page in sns.Paginators.ListTopics(new ListTopicsRequest()).Responses)
        {
            listed.UnionWith((page.Topics ?? []).Select(t => t.TopicArn));
        }

        Ensure(!listed.Contains(arn), "ListTopics still lists the deleted topic");
        await Refused(() => sns.PublishAsync(new PublishRequest { TopicArn = arn, Message = "gone" }), nf, 404, "Publish to deleted topic", "Topic does not exist");
    }

    private static async Task GoneSubscription(FidelityContext ctx, string subArn, string topicArn)
    {
        var sns = ctx.Sns;
        await sns.UnsubscribeAsync(new UnsubscribeRequest { SubscriptionArn = subArn });
        string[] nf = ["NotFound"];
        await PollRefused(() => sns.GetSubscriptionAttributesAsync(new GetSubscriptionAttributesRequest { SubscriptionArn = subArn }), nf, 404,
            "GetSubscriptionAttributes", "Subscription does not exist");
        var listed = new HashSet<string>();
        await foreach (var page in sns.Paginators.ListSubscriptionsByTopic(new ListSubscriptionsByTopicRequest { TopicArn = topicArn }).Responses)
        {
            listed.UnionWith((page.Subscriptions ?? []).Select(s => s.SubscriptionArn));
        }

        Ensure(!listed.Contains(subArn), "ListSubscriptionsByTopic still lists the removed subscription");
        await Refused(() => sns.SetSubscriptionAttributesAsync(new SetSubscriptionAttributesRequest
            {
                SubscriptionArn = subArn, AttributeName = "RawMessageDelivery", AttributeValue = "true",
            }), nf, 404,
            "SetSubscriptionAttributes on removed subscription", "Subscription does not exist");
    }

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
