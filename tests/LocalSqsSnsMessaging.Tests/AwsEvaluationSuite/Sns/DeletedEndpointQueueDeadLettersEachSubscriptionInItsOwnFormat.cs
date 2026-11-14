// Translated from behavioral/sns/check_deleted_endpoint_queue_dead_letters_each_subscription_in_its_own_format.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Web;
using Amazon.SimpleNotificationService.Model;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;
using SnsMessageAttributeValue = Amazon.SimpleNotificationService.Model.MessageAttributeValue;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sns;

/// <summary>sns.deleted-endpoint-queue-dead-letters-each-subscription-in-its-own-format</summary>
[Category("AwsEvaluationSuite")]
public sealed class DeletedEndpointQueueDeadLettersEachSubscriptionInItsOwnFormat
{
    private const string Scenario = "sns-deleted-endpoint-queue-dead-letters-each";

    /// <summary>
    /// With the subscribed queue deleted, each redrive-enabled subscription dead-letters in its own
    /// format: the enveloped copy is the full notification naming its subscription, the raw copy is
    /// the bare body with SQS message attributes. A delivered publish is not dead-lettered.
    /// </summary>
    [Test]
    [DisplayName("sns.deleted-endpoint-queue-dead-letters-each-subscription-in-its-own-format")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sns = ctx.Sns;
        var sqs = ctx.Sqs;
        var topicArn = await CreateTopic(ctx);
        var (dlqUrl, dlqArn) = await CreateQueueForTopic(ctx, topicArn);
        var (envUrl, envArn) = await CreateQueueForTopic(ctx, topicArn);
        var (rawUrl, rawArn) = await CreateQueueForTopic(ctx, topicArn);
        var envSub = await SubscribeAndConfirm(ctx, topicArn, envArn);
        var rawSub = await SubscribeAndConfirm(ctx, topicArn, rawArn, attributes: new() { ["RawMessageDelivery"] = "true" });
        foreach (var sub in new[] { envSub, rawSub })
        {
            await SetRedrive(ctx, sub, dlqArn);
        }

        var healthy = FidelityContext.UniqueName("sim2real-behav-sns-dlqdel-healthy-");
        await sns.PublishAsync(new PublishRequest { TopicArn = topicArn, Message = healthy });
        await ReceiveKeyed(ctx, envUrl, EnvelopeMessage, [healthy], timeoutS: 60.0,
            description: "control: the enveloped endpoint receives while it exists");
        await ReceiveKeyed(ctx, rawUrl, m => m.Body, [healthy], timeoutS: 60.0,
            description: "control: the raw endpoint receives while it exists");
        var early = await Drain(ctx, dlqUrl, windowS: 15.0);
        Ensure(early.Count == 0, $"a delivered message must not also be dead-lettered, got {Repr(early)}");

        await sqs.DeleteQueueAsync(new DeleteQueueRequest { QueueUrl = envUrl });
        await sqs.DeleteQueueAsync(new DeleteQueueRequest { QueueUrl = rawUrl });

        var attrs = new Dictionary<string, SnsMessageAttributeValue> { ["n"] = new() { DataType = "Number", StringValue = "+5" } };
        // DeleteQueue propagates on SQS's side; republish (fresh marker each time) until one
        // publish has been dead-lettered by both subscriptions, then grade that one.
        string dead = "";
        Dictionary<string, Message> copies = [];
        var deadLettered = false;
        for (var attempt = 0; attempt < 6; attempt++)
        {
            dead = FidelityContext.UniqueName("sim2real-behav-sns-dlqdel-dead-");
            await sns.PublishAsync(new PublishRequest { TopicArn = topicArn, Message = dead, Subject = "dead-letter", MessageAttributes = attrs });
            copies = await CollectKeyed(ctx, dlqUrl, DlqKey, [$"env:{dead}", $"raw:{dead}"], windowS: 30.0);
            if (copies.ContainsKey($"env:{dead}") && copies.ContainsKey($"raw:{dead}"))
            {
                deadLettered = true;
                break;
            }
        }

        if (!deadLettered)
        {
            throw Fail("no publish to the deleted endpoints was dead-lettered by both subscriptions " +
                $"across 6 attempts; last attempt's DLQ keys {Repr(copies.Keys.Select(k => k.Length <= 60 ? k : k[..60]).Order(StringComparer.Ordinal))}");
        }

        var envelope = JsonNode.Parse(copies[$"env:{dead}"].Body)!.AsObject();
        var unsubscribeUrl = new Uri((string)envelope["UnsubscribeURL"]!);
        var unsubscribe = HttpUtility.ParseQueryString(unsubscribeUrl.Query).GetValues("SubscriptionArn");
        Ensure(unsubscribe is [var only] && only == envSub,
            "the enveloped DLQ copy must name the subscription that failed in its UnsubscribeURL; " +
            $"got {(unsubscribe is [var r] && r == rawSub ? "the raw sibling" : unsubscribe is null ? "None" : Repr(unsubscribe))}");
        var gotType = (string?)envelope["Type"];
        var gotTopicMatches = (string?)envelope["TopicArn"] == topicArn;
        var gotSubject = (string?)envelope["Subject"];
        var gotAttributes = envelope["MessageAttributes"];
        var wantAttributes = new JsonObject { ["n"] = new JsonObject { ["Type"] = "Number", ["Value"] = "+5" } };
        Ensure(gotType == "Notification" && gotTopicMatches && gotSubject == "dead-letter" && JsonNode.DeepEquals(gotAttributes, wantAttributes),
            "the enveloped DLQ copy must be the full notification with Subject and attributes " +
            $"verbatim; got Type/TopicArn-matches/Subject/attributes ('{gotType}', {(gotTopicMatches ? "True" : "False")}, '{gotSubject}', {gotAttributes?.ToJsonString() ?? "None"})");
        var rawCopy = copies[$"raw:{dead}"];
        var rawAttrs = (rawCopy.MessageAttributes ?? []).ToDictionary(kv => kv.Key, kv => (kv.Value.DataType, kv.Value.StringValue));
        Ensure(rawAttrs.Count == 1 && rawAttrs.TryGetValue("n", out var n) && n == ("Number", "5"),
            $"the raw DLQ copy must carry the attribute as an SQS Number attribute, got {{{string.Join(", ", rawAttrs.Select(kv => $"'{kv.Key}': ('{kv.Value.DataType}', '{kv.Value.StringValue}')"))}}}");
        var extra = (await Drain(ctx, dlqUrl, windowS: 10.0)).Where(b => b.Contains(dead, StringComparison.Ordinal)).ToList();
        Ensure(extra.Count == 0, $"each subscription dead-letters one copy, extra copies {Repr(extra)}");
    }

    private static string Repr(IEnumerable<string> items) => "[" + string.Join(", ", items.Select(i => $"'{i}'")) + "]";

    private static string Head(string s, int n) => s.Length <= n ? s : s[..n];

    private static List<Amazon.SimpleNotificationService.Model.Tag> TagsKv() =>
        [new() { Key = "sim2real", Value = Scenario }, new() { Key = "sim2real:managed", Value = "true" }];

    /// <summary>CreateTopic (tagged + prefix-named) and register DeleteTopic immediately.</summary>
    private static async Task<string> CreateTopic(FidelityContext ctx, bool fifo = false)
    {
        var name = FidelityContext.UniqueName("sim2real-behav-sns-t-");
        var attrs = new Dictionary<string, string>();
        if (fifo)
        {
            name += ".fifo";
            attrs["FifoTopic"] = "true";
        }

        var arn = (await ctx.Sns.CreateTopicAsync(new CreateTopicRequest { Name = name, Attributes = attrs, Tags = TagsKv() })).TopicArn;
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
            QueueName = name,
            Attributes = attrs,
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
        var error = await FidelityContext.CaptureError(() => ctx.Sqs.DeleteQueueAsync(new DeleteQueueRequest { QueueUrl = url }));
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

    /// <summary>Bounded retry; raises on the deadline.</summary>
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
    /// Long-poll until a message keyed <c>key(msg)</c> exists for every wanted key; raises on the
    /// deadline. Each message is returned whole (Body plus MessageAttributes).
    /// </summary>
    private static async Task<Dictionary<string, Message>> ReceiveKeyed(FidelityContext ctx, string queueUrl, Func<Message, string?> key,
        HashSet<string> wanted, double timeoutS, string description)
    {
        var sqs = ctx.Sqs;
        var got = new Dictionary<string, Message>(StringComparer.Ordinal);
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
                throw Fail($"{description}: {Repr(wanted.Except(got.Keys).Order(StringComparer.Ordinal))} never arrived within " +
                    $"{timeoutS}s; other bodies seen: {Repr(other)}");
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

    /// <summary>Point the subscription's RedrivePolicy at the DLQ and wait for the read to agree.</summary>
    private static async Task SetRedrive(FidelityContext ctx, string subArn, string dlqArn)
    {
        var sns = ctx.Sns;
        var want = new JsonObject { ["deadLetterTargetArn"] = dlqArn };
        await sns.SetSubscriptionAttributesAsync(new SetSubscriptionAttributesRequest
        {
            SubscriptionArn = subArn, AttributeName = "RedrivePolicy", AttributeValue = want.ToJsonString(),
        });
        await PollUntil(
            async () => (await sns.GetSubscriptionAttributesAsync(new GetSubscriptionAttributesRequest { SubscriptionArn = subArn }))
                .Attributes?.GetValueOrDefault("RedrivePolicy"),
            raw => !string.IsNullOrEmpty(raw) && JsonNode.DeepEquals(JsonNode.Parse(raw), want),
            timeoutS: 30.0, intervalS: 1.0,
            description: "GetSubscriptionAttributes reflects the RedrivePolicy");
    }

    /// <summary>
    /// Long-poll until every wanted key has arrived or the window ends; returns what arrived
    /// (possibly partial). The caller decides whether partial is a failure.
    /// </summary>
    private static async Task<Dictionary<string, Message>> CollectKeyed(FidelityContext ctx, string queueUrl, Func<Message, string> key,
        HashSet<string> wanted, double windowS)
    {
        var sqs = ctx.Sqs;
        var got = new Dictionary<string, Message>(StringComparer.Ordinal);
        var deadline = FidelityTime.StartNew();
        while (!wanted.IsSubsetOf(got.Keys) && deadline.Elapsed < TimeSpan.FromSeconds(windowS))
        {
            var resp = await sqs.ReceiveMessageAsync(new ReceiveMessageRequest
            {
                QueueUrl = queueUrl, MaxNumberOfMessages = 10, WaitTimeSeconds = 5,
                MessageAttributeNames = ["All"], MessageSystemAttributeNames = ["All"],
            });
            foreach (var msg in resp.Messages ?? [])
            {
                await sqs.DeleteMessageAsync(new DeleteMessageRequest { QueueUrl = queueUrl, ReceiptHandle = msg.ReceiptHandle });
                got.TryAdd(key(msg), msg);
            }
        }

        return got;
    }

    /// <summary>'env:&lt;Message&gt;' for an enveloped copy, 'raw:&lt;Body&gt;' for a raw one.</summary>
    private static string DlqKey(Message msg)
    {
        var inner = EnvelopeMessage(msg);
        return inner is not null ? $"env:{inner}" : $"raw:{msg.Body}";
    }
}
