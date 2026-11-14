// Translated from behavioral/sns/check_raw_delivery_toggle_on_a_live_subscription_switches_the_delivered_format.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Text.Json;
using System.Text.Json.Nodes;
using Amazon.SimpleNotificationService.Model;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;
using SnsMessageAttributeValue = Amazon.SimpleNotificationService.Model.MessageAttributeValue;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sns;

/// <summary>sns.raw-delivery-toggle-on-a-live-subscription-switches-the-delivered-format</summary>
[Category("AwsEvaluationSuite")]
public sealed class RawDeliveryToggleOnALiveSubscriptionSwitchesTheDeliveredFormat
{
    private const string Scenario = "sns-raw-delivery-toggle-on-a-live-subscripti";

    /// <summary>
    /// Toggling RawMessageDelivery on a live subscription switches delivery from the JSON envelope
    /// to the bare body with SQS message attributes, and back again, without resubscribing.
    /// </summary>
    [Test]
    [DisplayName("sns.raw-delivery-toggle-on-a-live-subscription-switches-the-delivered-format")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    [KnownFidelityGap(FidelityGap.SnsEnvelope, "an enveloped delivery adds a TopicArn SQS message attribute that SNS doesn't send")]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sns = ctx.Sns;
        var topicArn = await CreateTopic(ctx);
        var (url, arn) = await CreateQueueForTopic(ctx, topicArn);
        var sub = await SubscribeAndConfirm(ctx, topicArn, arn);
        Dictionary<string, SnsMessageAttributeValue> Attrs() => new() { ["origin"] = new SnsMessageAttributeValue { DataType = "String", StringValue = "toggle" } };
        var expectedEnvelopeAttrs = new JsonObject { ["origin"] = new JsonObject { ["Type"] = "String", ["Value"] = "toggle" } };

        static bool IsRaw(Message msg) => EnvelopeMessage(msg) is null;

        async Task<(string Marker, Message Msg)> Deliver(string prefix)
        {
            var marker = FidelityContext.UniqueName(prefix);
            await sns.PublishAsync(new PublishRequest { TopicArn = topicArn, Message = marker, MessageAttributes = Attrs() });
            return (marker, (await ReceiveKeyed(ctx, url, m => EnvelopeMessage(m) ?? m.Body, [marker], timeoutS: 60.0,
                description: $"{marker} is delivered"))[marker]);
        }

        // The attribute change is eventually consistent, so allow a few deliveries in the old format.
        async Task<(string Marker, Message Msg)> DeliverUntil(string prefix, bool wantRaw, int attempts = 6)
        {
            var seen = new List<string>();
            for (var i = 0; i < attempts; i++)
            {
                var (marker, msg) = await Deliver(prefix);
                if (IsRaw(msg) == wantRaw)
                {
                    return (marker, msg);
                }

                seen.Add(IsRaw(msg) ? "raw" : "envelope");
            }

            throw Fail($"after setting RawMessageDelivery={(wantRaw ? "true" : "false")}, {attempts} deliveries " +
                $"never switched format; formats seen {List(seen)}");
        }

        var (marker, before) = await Deliver("sim2real-behav-sns-toggle-before-");
        var envelope = JsonNode.Parse(before.Body)!.AsObject();
        Ensure((string?)Required(envelope, "Message") == marker && JsonNode.DeepEquals(Required(envelope, "MessageAttributes"), expectedEnvelopeAttrs),
            $"before the toggle the subscription must receive the envelope, got '{Head(before.Body, 200)}'");

        await sns.SetSubscriptionAttributesAsync(new SetSubscriptionAttributesRequest
        {
            SubscriptionArn = sub, AttributeName = "RawMessageDelivery", AttributeValue = "true",
        });
        (marker, var raw) = await DeliverUntil("sim2real-behav-sns-toggle-raw-", true);
        var rawAttrs = (raw.MessageAttributes ?? []).ToDictionary(kv => kv.Key, kv => (kv.Value.DataType, kv.Value.StringValue));
        Ensure(raw.Body == marker && rawAttrs.Count == 1 && rawAttrs.GetValueOrDefault("origin") == ("String", "toggle"),
            "with RawMessageDelivery=true the body must be the bare message and the attribute an SQS " +
            $"attribute; got body '{Head(raw.Body, 200)}', attributes " +
            "{" + string.Join(", ", rawAttrs.Select(kv => $"'{kv.Key}': ('{kv.Value.DataType}', '{kv.Value.StringValue}')")) + "}");

        await sns.SetSubscriptionAttributesAsync(new SetSubscriptionAttributesRequest
        {
            SubscriptionArn = sub, AttributeName = "RawMessageDelivery", AttributeValue = "false",
        });
        (marker, var back) = await DeliverUntil("sim2real-behav-sns-toggle-back-", false);
        envelope = JsonNode.Parse(back.Body)!.AsObject();
        Ensure((string?)Required(envelope, "Message") == marker
            && JsonNode.DeepEquals(Required(envelope, "MessageAttributes"), expectedEnvelopeAttrs)
            && back.MessageAttributes is null,
            "after turning raw delivery off the envelope must return, with the attribute inside it " +
            $"and none on the SQS message; got '{Head(back.Body, 200)}', SQS attributes " +
            (back.MessageAttributes is null ? "None" :
                "{" + string.Join(", ", back.MessageAttributes.Select(kv => $"'{kv.Key}': {{'StringValue': '{kv.Value.StringValue}', 'DataType': '{kv.Value.DataType}'}}")) + "}"));
    }

    /// <summary><c>envelope[key]</c>: a missing key raises KeyError, as in Python.</summary>
    private static JsonNode? Required(JsonObject obj, string key) =>
        obj.TryGetPropertyValue(key, out var value) ? value : throw new KeyNotFoundException(key);

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
