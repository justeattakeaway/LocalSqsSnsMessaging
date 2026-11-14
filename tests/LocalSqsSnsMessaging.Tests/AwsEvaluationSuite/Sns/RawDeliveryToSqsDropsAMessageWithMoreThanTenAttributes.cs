// Translated from behavioral/sns/check_raw_delivery_to_sqs_drops_a_message_with_more_than_ten_attributes.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Globalization;
using System.Text.Json.Nodes;
using Amazon.SimpleNotificationService.Model;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;
using SnsMessageAttributeValue = Amazon.SimpleNotificationService.Model.MessageAttributeValue;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sns;

/// <summary>sns.raw-delivery-to-sqs-drops-a-message-with-more-than-ten-attributes</summary>
[Category("AwsEvaluationSuite")]
public sealed class RawDeliveryToSqsDropsAMessageWithMoreThanTenAttributes
{
    private const string Scenario = "sns-raw-attr-limit";

    /// <summary>
    /// Raw delivery maps SNS message attributes onto SQS ones, which hold at most ten: an
    /// eleven-attribute message is not delivered to the raw subscriber but reaches the enveloped
    /// twin, while a ten-attribute message reaches both.
    /// </summary>
    [Test]
    [DisplayName("sns.raw-delivery-to-sqs-drops-a-message-with-more-than-ten-attributes")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    [KnownFidelityGap(FidelityGap.SnsRawDelivery, "Raw delivery hands SQS a message with eleven message attributes instead of dropping it")]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sns = ctx.Sns;
        var sqs = ctx.Sqs;
        var topicArn = await CreateTopic(ctx);
        var (rawUrl, rawArn) = await CreateQueueForTopic(ctx, topicArn);
        var (envUrl, envArn) = await CreateQueueForTopic(ctx, topicArn);
        await SubscribeAndConfirm(ctx, topicArn, rawArn, attributes: new() { ["RawMessageDelivery"] = "true" });
        await SubscribeAndConfirm(ctx, topicArn, envArn);

        var eleven = FidelityContext.UniqueName("sim2real-behav-rawlimit-eleven-");
        var ten = FidelityContext.UniqueName("sim2real-behav-rawlimit-ten-");
        var started = FidelityTime.StartNew();
        await sns.PublishAsync(new PublishRequest
        {
            TopicArn = topicArn,
            Message = eleven,
            MessageAttributes = Enumerable.Range(0, 11).ToDictionary(i => $"a{i:00}", i => S($"v{i}")),
        });
        await sns.PublishAsync(new PublishRequest
        {
            TopicArn = topicArn,
            Message = ten,
            MessageAttributes = Enumerable.Range(0, 10).ToDictionary(i => $"a{i:00}", i => S($"v{i}")),
        });

        var enveloped = new Dictionary<string, JsonObject>();
        var deadline = FidelityTime.StartNew();
        while (!enveloped.Keys.ToHashSet().SetEquals([eleven, ten]))
        {
            foreach (var msg in (await sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = envUrl, MaxNumberOfMessages = 10, WaitTimeSeconds = 5 })).Messages ?? [])
            {
                await sqs.DeleteMessageAsync(new DeleteMessageRequest { QueueUrl = envUrl, ReceiptHandle = msg.ReceiptHandle });
                var env = JsonNode.Parse(msg.Body)!.AsObject();
                enveloped[(string)env["Message"]!] = env;
            }

            if (!enveloped.Keys.ToHashSet().SetEquals([eleven, ten]) && deadline.Elapsed >= TimeSpan.FromSeconds(120.0))
            {
                throw Fail($"the enveloped twin received only {Show(enveloped.Keys.Order(StringComparer.Ordinal))}");
            }
        }

        var latency = Math.Max(started.Elapsed.TotalSeconds, 1.0);
        var elevenAttrs = (enveloped[eleven]["MessageAttributes"] as JsonObject)?.Select(kv => kv.Key).ToList() ?? [];
        Ensure(elevenAttrs.Count == 11,
            $"the enveloped copy must carry all eleven attributes, got " +
            $"{Show(elevenAttrs.Order(StringComparer.Ordinal))}");

        var raw = new List<Message>();
        deadline = FidelityTime.StartNew();
        var rawLimit = Math.Max(60.0, 3.0 * latency);
        while (!raw.Select(m => m.Body).Contains(ten))
        {
            foreach (var msg in (await sqs.ReceiveMessageAsync(new ReceiveMessageRequest
            {
                QueueUrl = rawUrl,
                MaxNumberOfMessages = 10,
                WaitTimeSeconds = 5,
                MessageAttributeNames = ["All"],
            })).Messages ?? [])
            {
                await sqs.DeleteMessageAsync(new DeleteMessageRequest { QueueUrl = rawUrl, ReceiptHandle = msg.ReceiptHandle });
                raw.Add(msg);
            }

            if (!raw.Select(m => m.Body).Contains(ten) && deadline.Elapsed >= TimeSpan.FromSeconds(rawLimit))
            {
                throw Fail($"the ten-attribute message never reached the raw subscriber; got " +
                    $"{Show(raw.Select(m => m.Body))}");
            }
        }

        var gotTen = raw.First(m => m.Body == ten);
        var tenAttrs = (gotTen.MessageAttributes ?? []).Keys.Order(StringComparer.Ordinal).ToList();
        Ensure(tenAttrs.SequenceEqual(Enumerable.Range(0, 10).Select(i => $"a{i:00}")),
            $"the raw copy must carry the ten attributes as SQS message attributes, got " +
            $"{Show(tenAttrs)}");
        var window = Math.Max(3.0 * latency, 15.0);
        var late = (await Drain(ctx, new() { ["raw"] = rawUrl }, windowS: window))["raw"];
        var bodies = raw.Select(m => m.Body).Concat(late).ToList();
        Ensure(!bodies.Contains(eleven),
            $"the eleven-attribute message must not reach the raw subscriber; within a " +
            $"{window.ToString("F0", CultureInfo.InvariantCulture)}s window it received {Show(bodies)}");
    }

    /// <summary>A String-typed message attribute value.</summary>
    private static SnsMessageAttributeValue S(string value) => new() { DataType = "String", StringValue = value };

    private static string Show(IEnumerable<string> values) => "[" + string.Join(", ", values.Select(v => $"'{v}'")) + "]";

    private static List<Amazon.SimpleNotificationService.Model.Tag> TagsKv() =>
        [new() { Key = "sim2real", Value = Scenario }, new() { Key = "sim2real:managed", Value = "true" }];

    /// <summary>CreateTopic (tagged + prefix-named) and register DeleteTopic immediately.</summary>
    private static async Task<string> CreateTopic(FidelityContext ctx)
    {
        var sns = ctx.Sns;
        var name = FidelityContext.UniqueName("sim2real-behav-sns-t-");
        var arn = (await sns.CreateTopicAsync(new CreateTopicRequest { Name = name, Attributes = [], Tags = TagsKv() })).TopicArn;
        ctx.OnCleanup(() => ctx.Sns.DeleteTopicAsync(new DeleteTopicRequest { TopicArn = arn }));
        return arn;
    }

    /// <summary>Create a real SQS queue whose policy lets exactly this topic send to it.</summary>
    private static async Task<(string Url, string Arn)> CreateQueueForTopic(FidelityContext ctx, string topicArn)
    {
        var sqs = ctx.Sqs;
        var name = FidelityContext.UniqueName("sim2real-behav-sns-q-");
        var url = (await sqs.CreateQueueAsync(new CreateQueueRequest
        {
            QueueName = name,
            Attributes = [],
            Tags = new() { ["sim2real"] = Scenario, ["sim2real:managed"] = "true" },
        })).QueueUrl;
        ctx.OnCleanup(() => ctx.Sqs.DeleteQueueAsync(url));
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
        var deadline = FidelityTime.StartNew();
        var pending = (await sns.GetSubscriptionAttributesAsync(new GetSubscriptionAttributesRequest { SubscriptionArn = subArn })).Attributes?.GetValueOrDefault("PendingConfirmation");
        while (pending != "false")
        {
            if (deadline.Elapsed >= TimeSpan.FromSeconds(timeoutS))
            {
                throw Fail($"the new subscription leaves PendingConfirmation: not satisfied within {timeoutS}s, last value {(pending is null ? "None" : $"'{pending}'")}");
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(2.0));
            pending = (await sns.GetSubscriptionAttributesAsync(new GetSubscriptionAttributesRequest { SubscriptionArn = subArn })).Attributes?.GetValueOrDefault("PendingConfirmation");
        }

        return subArn;
    }

    /// <summary>
    /// Alternate long polls across the queues for one shared window and return every body
    /// each produced. The empty lists feed an equality-to-empty assertion.
    /// </summary>
    private static async Task<Dictionary<string, List<string>>> Drain(FidelityContext ctx, Dictionary<string, string> queues, double windowS)
    {
        var sqs = ctx.Sqs;
        var output = queues.Keys.ToDictionary(label => label, _ => new List<string>());
        var deadline = FidelityTime.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(windowS))
        {
            foreach (var (label, url) in queues)
            {
                var resp = await sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = url, MaxNumberOfMessages = 10, WaitTimeSeconds = 2 });
                foreach (var msg in resp.Messages ?? [])
                {
                    await sqs.DeleteMessageAsync(new DeleteMessageRequest { QueueUrl = url, ReceiptHandle = msg.ReceiptHandle });
                    output[label].Add(msg.Body);
                }
            }
        }

        return output;
    }
}
