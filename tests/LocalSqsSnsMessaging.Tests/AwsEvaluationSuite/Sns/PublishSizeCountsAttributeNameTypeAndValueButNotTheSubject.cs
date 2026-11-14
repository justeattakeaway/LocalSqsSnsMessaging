// Translated from behavioral/sns/check_publish_size_counts_attribute_name_type_and_value_but_not_the_subject.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Text.Json.Nodes;
using Amazon.SimpleNotificationService.Model;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;
using SnsMessageAttributeValue = Amazon.SimpleNotificationService.Model.MessageAttributeValue;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sns;

/// <summary>sns.publish-size-counts-attribute-name-type-and-value-but-not-the-subject</summary>
[Category("AwsEvaluationSuite")]
public sealed class PublishSizeCountsAttributeNameTypeAndValueButNotTheSubject
{
    private const string Scenario = "sns-publish-size-counts-attribute-name-type-";

    /// <summary>
    /// The 262144-byte Publish limit charges body plus each attribute's name, DataType and value,
    /// not the Subject: exactly at the limit is accepted and delivered, one byte over is refused.
    /// </summary>
    [Test]
    [DisplayName("sns.publish-size-counts-attribute-name-type-and-value-but-not-the-subject")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sns = ctx.Sns;
        const int limit = 262144;
        var topicArn = await CreateTopic(ctx);
        var (url, arn) = await CreateQueueForTopic(ctx, topicArn);
        await SubscribeAndConfirm(ctx, topicArn, arn, attributes: new() { ["RawMessageDelivery"] = "true" });

        // Two attribute shapes pin the rule to name + type + value rather than value alone.
        var shapes = new (string Name, string DType, string Value, char Fill)[]
        {
            ("n", "String", new string('v', 10), 'p'),   // 1 + 6 + 10 = 17 bytes charged
            ("abc", "Number", "12345", 'q'),             // 3 + 6 + 5 = 14 bytes charged
        };
        var expectedBodies = new Dictionary<string, (string Body, Dictionary<string, (string?, string?)> Attrs)>();
        foreach (var (name, dtype, value, fill) in shapes)
        {
            var charged = name.Length + dtype.Length + value.Length;
            Dictionary<string, SnsMessageAttributeValue> Attrs() => new() { [name] = new SnsMessageAttributeValue { DataType = dtype, StringValue = value } };
            var (code, message) = await Error(() => sns.PublishAsync(new PublishRequest
            {
                TopicArn = topicArn, Message = new string(fill, limit - charged + 1), MessageAttributes = Attrs(),
            }));
            Ensure((code, message) == ("InvalidParameter", "Invalid parameter: Message too long"),
                $"a body of {limit - charged + 1} bytes with attribute '{name}'/'{dtype}' " +
                $"({charged} bytes charged) is one byte over and must be refused; got " +
                $"'{code}' '{message}'");
            var body = new string(fill, limit - charged);
            var resp = await sns.PublishAsync(new PublishRequest { TopicArn = topicArn, Message = body, MessageAttributes = Attrs() });
            Ensure(!string.IsNullOrEmpty(resp.MessageId),
                $"a body of {body.Length} bytes plus {charged} attribute bytes totals exactly {limit} " +
                $"and must be accepted; got {resp}");
            expectedBodies[fill.ToString()] = (body, new() { [name] = (dtype, value) });
        }

        var subjectBody = new string('s', limit);
        var subjectResp = await sns.PublishAsync(new PublishRequest { TopicArn = topicArn, Message = subjectBody, Subject = new string('t', 100) });
        Ensure(!string.IsNullOrEmpty(subjectResp.MessageId),
            $"the Subject is not charged: a {limit}-byte body with a 100-character Subject must be " +
            $"accepted; got {subjectResp}");
        expectedBodies["s"] = (subjectBody, []);

        var got = await ReceiveKeyed(ctx, url, m => m.Body[..Math.Min(1, m.Body.Length)], [.. expectedBodies.Keys], timeoutS: 120.0,
            description: "every exactly-at-the-limit publish is delivered");
        foreach (var (fill, (body, attrs)) in expectedBodies)
        {
            var msg = got[fill];
            var deliveredAttrs = (msg.MessageAttributes ?? []).ToDictionary(kv => kv.Key, kv => ((string?)kv.Value.DataType, (string?)kv.Value.StringValue));
            var attrsEqual = deliveredAttrs.Count == attrs.Count && deliveredAttrs.All(kv => attrs.TryGetValue(kv.Key, out var a) && a == kv.Value);
            Ensure(msg.Body == body && attrsEqual,
                $"the at-the-limit publish '{fill}' must be delivered intact: body " +
                $"{msg.Body.Length} of {body.Length} bytes, attributes {Repr(deliveredAttrs)} vs {Repr(attrs)}");
        }
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
}
