// Translated from behavioral/sns/check_raw_message_delivery_strips_the_envelope_for_only_that_subscription.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Text.Json;
using System.Text.Json.Nodes;
using Amazon.SimpleNotificationService.Model;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sns;

/// <summary>sns.raw-message-delivery-strips-the-envelope-for-only-that-subscription</summary>
[Category("AwsEvaluationSuite")]
public sealed class RawMessageDeliveryStripsTheEnvelopeForOnlyThatSubscription
{
    private const string Scenario = "sns-raw-delivery";

    /// <summary>
    /// One publish to a topic with two SQS subscriptions delivers the bare body to the
    /// RawMessageDelivery=true subscription and the full JSON envelope to the other.
    /// </summary>
    [Test]
    [DisplayName("sns.raw-message-delivery-strips-the-envelope-for-only-that-subscription")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sns = ctx.Sns;
        var topicArn = await CreateTopic(ctx);
        var (rawUrl, rawArn) = await CreateQueueForTopic(ctx, topicArn);
        var (wrappedUrl, wrappedArn) = await CreateQueueForTopic(ctx, topicArn);

        var rawSub = await SubscribeAndConfirm(ctx, topicArn, rawArn, attributes: new() { ["RawMessageDelivery"] = "true" });
        var wrappedSub = await SubscribeAndConfirm(ctx, topicArn, wrappedArn);
        await AwaitAttribute(ctx, rawSub, "RawMessageDelivery", "true");

        var wrappedAttrs = (await sns.GetSubscriptionAttributesAsync(new GetSubscriptionAttributesRequest { SubscriptionArn = wrappedSub })).Attributes ?? [];
        Ensure(wrappedAttrs.GetValueOrDefault("RawMessageDelivery", "false") == "false",
            "the control subscription must not have RawMessageDelivery set, else the differential " +
            $"proves nothing: '{wrappedAttrs.GetValueOrDefault("RawMessageDelivery")}'");

        var payload = FidelityContext.UniqueName("sim2real-behav-sns-rawpay-");
        var messageId = (await sns.PublishAsync(new PublishRequest { TopicArn = topicArn, Message = payload })).MessageId;

        var rawBody = await AwaitBody(ctx, rawUrl, payload);
        var wrappedBody = await AwaitBody(ctx, wrappedUrl, payload);

        Ensure(rawBody == payload, $"RawMessageDelivery still wrapped the message: '{Head(rawBody, 200)}'");
        var envelope = JsonNode.Parse(wrappedBody)!.AsObject();
        Ensure((string?)envelope["Message"] == payload, $"the enveloped subscription did not carry the message: '{Head(wrappedBody, 200)}'");
        Ensure(wrappedBody != rawBody, "both subscriptions delivered the same payload, so RawMessageDelivery had no effect");
        Ensure((string?)envelope["MessageId"] == messageId, $"envelope MessageId '{envelope["MessageId"]}' != Publish MessageId '{messageId}'");
        Ensure((string?)envelope["Type"] == "Notification", $"the non-raw subscription must receive a Notification envelope: '{Head(wrappedBody, 200)}'");
    }

    private static string Head(string s, int n) => s.Length <= n ? s : s[..n];

    private static List<Amazon.SimpleNotificationService.Model.Tag> TagsKv() =>
        [new() { Key = "sim2real", Value = Scenario }, new() { Key = "sim2real:managed", Value = "true" }];

    /// <summary>CreateTopic (tagged + prefix-named) and register DeleteTopic immediately; returns the ARN.</summary>
    private static async Task<string> CreateTopic(FidelityContext ctx, bool fifo = false, Dictionary<string, string>? attributes = null)
    {
        var sns = ctx.Sns;
        var name = FidelityContext.UniqueName("sim2real-behav-sns-t-");
        var attrs = new Dictionary<string, string>(attributes ?? []);
        if (fifo)
        {
            name += ".fifo";
            attrs["FifoTopic"] = "true";
        }

        var arn = (await sns.CreateTopicAsync(new CreateTopicRequest { Name = name, Attributes = attrs, Tags = TagsKv() })).TopicArn;
        ctx.OnCleanup(() => ctx.Sns.DeleteTopicAsync(new DeleteTopicRequest { TopicArn = arn }));
        return arn;
    }

    /// <summary>Create a real SQS queue whose policy lets exactly this topic publish to it.</summary>
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
        if (attributes is not null)
        {
            request.Attributes = attributes;
        }

        var subArn = (await sns.SubscribeAsync(request)).SubscriptionArn;
        ctx.OnCleanup(() => ctx.Sns.UnsubscribeAsync(new UnsubscribeRequest { SubscriptionArn = subArn }));
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

        throw Fail($"subscription '{subArn}' was still PendingConfirmation='{pending}' after {timeoutS}s");
    }

    /// <summary>Poll GetSubscriptionAttributes until the named attribute reads back as want.</summary>
    private static async Task AwaitAttribute(FidelityContext ctx, string subArn, string name, string want, double timeoutS = 60.0)
    {
        var sns = ctx.Sns;
        var deadline = FidelityTime.StartNew();
        string? got = null;
        while (deadline.Elapsed < TimeSpan.FromSeconds(timeoutS))
        {
            got = (await sns.GetSubscriptionAttributesAsync(new GetSubscriptionAttributesRequest { SubscriptionArn = subArn }))
                .Attributes?.GetValueOrDefault(name);
            if (got == want)
            {
                return;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(1.0));
        }

        throw Fail($"{name} on '{subArn}' was '{got}', not '{want}', after {timeoutS}s");
    }

    /// <summary>Long-poll the queue until a body containing needle arrives; returns that body.</summary>
    private static async Task<string> AwaitBody(FidelityContext ctx, string queueUrl, string needle, double timeoutS = 90.0)
    {
        var sqs = ctx.Sqs;
        var seen = new List<string>();
        var deadline = FidelityTime.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(timeoutS))
        {
            var resp = await sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = queueUrl, MaxNumberOfMessages = 10, WaitTimeSeconds = 5 });
            foreach (var msg in resp.Messages ?? [])
            {
                await sqs.DeleteMessageAsync(new DeleteMessageRequest { QueueUrl = queueUrl, ReceiptHandle = msg.ReceiptHandle });
                var body = msg.Body ?? "";
                seen.Add(body);
                if (body.Contains(needle, StringComparison.Ordinal))
                {
                    return body;
                }
            }
        }

        throw Fail($"no message containing '{needle}' was delivered within {timeoutS}s; " +
            $"bodies seen: [{string.Join(", ", seen.Select(b => $"'{Head(b, 120)}'"))}]");
    }
}
