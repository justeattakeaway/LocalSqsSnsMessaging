// Translated from behavioral/sns/check_raw_delivery_normalises_number_attributes_the_envelope_carries_verbatim.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Text.Json;
using System.Text.Json.Nodes;
using Amazon.SimpleNotificationService.Model;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;
using SnsMessageAttributeValue = Amazon.SimpleNotificationService.Model.MessageAttributeValue;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sns;

/// <summary>sns.raw-delivery-normalises-number-attributes-the-envelope-carries-verbatim</summary>
[Category("AwsEvaluationSuite")]
public sealed class RawDeliveryNormalisesNumberAttributesTheEnvelopeCarriesVerbatim
{
    private const string Scenario = "sns-raw-delivery-normalises-number-attribute";
    private const string NoSuchQueue = "AWS.SimpleQueueService.NonExistentQueue";

    /// <summary>
    /// A Number attribute published as `+5`, `.5`, `1.` or `1.500` keeps that exact spelling in the
    /// JSON envelope, while the raw subscriber of the same publish receives it as an SQS Number
    /// attribute in canonical form (`5`, `0.5`, `1`, `1.5`).
    /// </summary>
    [Test]
    [DisplayName("sns.raw-delivery-normalises-number-attributes-the-envelope-carries-verbatim")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    [KnownFidelityGap(FidelityGap.SnsRawDelivery, "raw delivery passes Number attributes through verbatim ('+5', '.5', '1.', '1.500') instead of in canonical form ('5', '0.5', '1', '1.5')")]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sns = ctx.Sns;
        var topicArn = await CreateTopic(ctx);
        var (envUrl, envArn) = await CreateQueueForTopic(ctx, topicArn);
        var (rawUrl, rawArn) = await CreateQueueForTopic(ctx, topicArn);
        await SubscribeAndConfirm(ctx, topicArn, envArn);
        await SubscribeAndConfirm(ctx, topicArn, rawArn, attributes: new() { ["RawMessageDelivery"] = "true" });

        var spellings = new Dictionary<string, (string Pub, string Canon)>
        {
            ["plus"] = ("+5", "5"), ["lead"] = (".5", "0.5"), ["trail"] = ("1.", "1"),
            ["zeros"] = ("1.500", "1.5"), ["plain"] = ("42", "42"),
        };
        var marker = FidelityContext.UniqueName("sim2real-behav-sns-numnorm-");
        await sns.PublishAsync(new PublishRequest
        {
            TopicArn = topicArn,
            Message = marker,
            MessageAttributes = spellings.ToDictionary(kv => kv.Key, kv => new SnsMessageAttributeValue { DataType = "Number", StringValue = kv.Value.Pub }),
        });

        var env = (await ReceiveKeyed(ctx, envUrl, EnvelopeMessage, [marker], timeoutS: 90.0,
            description: "the enveloped subscriber receives the publish"))[marker];
        var envAttrs = JsonNode.Parse(env.Body)!["MessageAttributes"];
        var wantEnv = new JsonObject();
        foreach (var (k, (pub, _)) in spellings)
        {
            wantEnv[k] = new JsonObject { ["Type"] = "Number", ["Value"] = pub };
        }

        Ensure(JsonNode.DeepEquals(envAttrs, wantEnv),
            $"the envelope must carry each Number exactly as published; expected {PyRepr(wantEnv)}, " +
            $"got {PyRepr(envAttrs)}");

        var raw = (await ReceiveKeyed(ctx, rawUrl, m => m.Body, [marker], timeoutS: 90.0,
            description: "the raw subscriber receives the publish"))[marker];
        var rawAttrs = (raw.MessageAttributes ?? []).ToDictionary(kv => kv.Key, kv => ((string?)kv.Value.DataType, (string?)kv.Value.StringValue));
        var wantRaw = spellings.ToDictionary(kv => kv.Key, kv => ((string?)"Number", (string?)kv.Value.Canon));
        Ensure(rawAttrs.Count == wantRaw.Count && rawAttrs.All(kv => wantRaw.TryGetValue(kv.Key, out var w) && w == kv.Value),
            "the raw subscriber must receive each Number as an SQS Number attribute in canonical " +
            $"form; expected {Show(wantRaw)}, got {Show(rawAttrs)}");
    }

    private static string Show(Dictionary<string, (string? DataType, string? StringValue)> d) =>
        $"{{{string.Join(", ", d.Select(kv => $"'{kv.Key}': ('{kv.Value.DataType}', '{kv.Value.StringValue}')"))}}}";

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
        if (error is not null && error.Code != NoSuchQueue)
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
    /// Long-poll until a message keyed `key(msg)` exists for every wanted key; RAISES on the
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
                    other.Add(msg.Body[..Math.Min(160, msg.Body.Length)]);
                }
            }

            if (!wanted.IsSubsetOf(got.Keys) && deadline.Elapsed >= TimeSpan.FromSeconds(timeoutS))
            {
                throw Fail(
                    $"{description}: [{string.Join(", ", wanted.Except(got.Keys).Order(StringComparer.Ordinal).Select(w => $"'{w}'"))}] never arrived within " +
                    $"{timeoutS}s; other bodies seen: [{string.Join(", ", other.Select(o => $"'{o}'"))}]");
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

        if (body is JsonObject obj && obj["Type"] is JsonValue type && type.GetValueKind() == JsonValueKind.String && type.GetValue<string>() == "Notification")
        {
            return obj["Message"] is JsonValue m && m.GetValueKind() == JsonValueKind.String ? m.GetValue<string>() : obj["Message"]?.ToJsonString();
        }

        return null;
    }
}
