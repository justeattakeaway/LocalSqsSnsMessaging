// Translated from behavioral/sns/check_publish_of_exactly_256_kib_delivered_intact_and_one_byte_more_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Amazon.SimpleNotificationService.Model;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sns;

/// <summary>sns.publish-of-exactly-256-kib-delivered-intact-and-one-byte-more-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class PublishOfExactly256KibDeliveredIntactAndOneByteMoreRejected
{
    private const string Scenario = "sns-publish-of-exactly-256-kib-delivered-int";

    /// <summary>
    /// Publish accepts exactly 262144 UTF-8 bytes and delivers them intact to raw and enveloped
    /// subscribers, but refuses 262145; with a 3-byte character, 87381 characters are accepted and
    /// delivered while 87382 are refused, so the limit is counted in bytes, not characters.
    /// </summary>
    [Test]
    [DisplayName("sns.publish-of-exactly-256-kib-delivered-intact-and-one-byte-more-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sns = ctx.Sns;
        const int limit = 262144;
        var topicArn = await CreateTopic(ctx);
        var (envUrl, envArn) = await CreateQueueForTopic(ctx, topicArn);
        var (rawUrl, rawArn) = await CreateQueueForTopic(ctx, topicArn);
        await SubscribeAndConfirm(ctx, topicArn, envArn);
        await SubscribeAndConfirm(ctx, topicArn, rawArn, attributes: new() { ["RawMessageDelivery"] = "true" });

        const string wide = "日";
        foreach (var body in new[] { new string('r', limit + 1), Repeat(wide, (limit / 3) + 1) })
        {
            var (code, message) = await Error(() => sns.PublishAsync(new PublishRequest { TopicArn = topicArn, Message = body }));
            Ensure((code, message) == ("InvalidParameter", "Invalid parameter: Message too long"),
                $"a {body.Length}-character, {Bytes(body)}-byte body must be refused with " +
                $"InvalidParameter 'Invalid parameter: Message too long', got '{code}' '{message}'");
        }

        var asciiMax = new string('a', limit);
        var wideMax = Repeat(wide, limit / 3);
        var sizes = (Bytes(asciiMax), Bytes(wideMax));
        Ensure(sizes == (limit, limit - 1), $"test-data construction error: sizes {sizes}");
        foreach (var body in new[] { asciiMax, wideMax })
        {
            var resp = await sns.PublishAsync(new PublishRequest { TopicArn = topicArn, Message = body });
            Ensure(!string.IsNullOrEmpty(resp.MessageId),
                $"a {Bytes(body)}-byte body is within the limit and must be accepted, got {resp}");
        }

        string BySize(Message m) => Str(Bytes(m.Body));
        var raw = await ReceiveKeyed(ctx, rawUrl, BySize, [Str(limit), Str(limit - 1)], timeoutS: 120.0,
            description: "the raw subscriber receives both maximum-size bodies");
        Ensure(raw[Str(limit)].Body == asciiMax, "the 262144-byte ASCII body must reach the raw subscriber byte-for-byte");
        Ensure(raw[Str(limit - 1)].Body == wideMax, "the 262143-byte three-byte-character body must reach the raw subscriber intact");

        var env = await ReceiveKeyed(ctx, envUrl, m => Str(Bytes(EnvelopeMessage(m) ?? "")),
            [Str(limit), Str(limit - 1)], timeoutS: 120.0,
            description: "the enveloped subscriber receives both maximum-size bodies");
        foreach (var (size, body) in new[] { (Str(limit), asciiMax), (Str(limit - 1), wideMax) })
        {
            var envelopeBytes = Bytes(env[size].Body);
            Ensure(EnvelopeMessage(env[size]) == body && envelopeBytes > limit,
                $"the {size}-byte body must arrive intact inside an envelope larger than the publish " +
                $"limit; envelope was {envelopeBytes} bytes");
        }

        var leaked = (await Drain(ctx, rawUrl, windowS: 10.0)).Select(b => b.Length).ToList();
        Ensure(leaked.Count == 0, $"a refused publish must deliver nothing, got bodies of length [{string.Join(", ", leaked)}]");
    }

    private static string Repeat(string s, int count) => string.Concat(Enumerable.Repeat(s, count));

    private static int Bytes(string s) => Encoding.UTF8.GetByteCount(s);

    private static string Str(int n) => n.ToString(CultureInfo.InvariantCulture);

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

    /// <summary>(Error.Code, Error.Message) the call raises. An unexpected success RAISES.</summary>
    private static async Task<(string Code, string Message)> Error<T>(Func<Task<T>> fn)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is null)
        {
            throw Fail($"expected the call to be refused, but it succeeded: {result}");
        }

        return (error.Code ?? "", error.Message ?? "");
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

    /// <summary>
    /// Long-poll for the whole window and return EVERY body seen, so the caller can assert on
    /// leakage rather than discard it.
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
                bodies.Add(Head(msg.Body, 300));
            }
        }

        return bodies;
    }
}
