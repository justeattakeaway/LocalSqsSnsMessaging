// Translated from behavioral/sns__sqs/check_platform_application_event_endpoint_created_notifies_configured_topic.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Globalization;
using System.Numerics;
using System.Text.Json.Nodes;
using Amazon.SimpleNotificationService.Model;
using Amazon.SQS;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.SnsSqs;

/// <summary>sns__sqs.platform-application-event-endpoint-created-notifies-configured-topic</summary>
[Category("AwsEvaluationSuite")]
public sealed class PlatformApplicationEventEndpointCreatedNotifiesConfiguredTopic
{
    private const string Check = "platform-application-event-endpoint-created-notifies-configured-topic";

    /// <summary>
    /// A platform application's EventEndpointCreated topic receives an EndpointCreated
    /// notification naming the EndpointArn when CreatePlatformEndpoint adds an endpoint.
    /// </summary>
    [Test]
    [DisplayName("sns__sqs.platform-application-event-endpoint-created-notifies-configured-topic")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sns = ctx.Sns;
        var sqs = ctx.Sqs;

        var topicArn = (await sns.CreateTopicAsync(new CreateTopicRequest
        {
            Name = FidelityContext.UniqueName("sim2real-behav-sns-appevt-t-"),
            Tags = [new Amazon.SimpleNotificationService.Model.Tag { Key = "sim2real", Value = Check }],
        })).TopicArn;
        ctx.OnCleanup(() => sns.DeleteTopicAsync(new DeleteTopicRequest { TopicArn = topicArn }));

        var queueUrl = (await sqs.CreateQueueAsync(new CreateQueueRequest
        {
            QueueName = FidelityContext.UniqueName("sim2real-behav-sns-appevt-q-"),
            Tags = new() { ["sim2real"] = Check },
        })).QueueUrl;
        ctx.OnCleanup(Teardown(() => sqs.DeleteQueueAsync(new DeleteQueueRequest { QueueUrl = queueUrl }),
            "AWS.SimpleQueueService.NonExistentQueue"));
        var queueArn = (await sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest
        {
            QueueUrl = queueUrl, AttributeNames = ["QueueArn"],
        })).Attributes!["QueueArn"];

        var policy = new JsonObject
        {
            ["Version"] = "2012-10-17",
            ["Statement"] = new JsonArray(new JsonObject
            {
                ["Effect"] = "Allow",
                ["Principal"] = new JsonObject { ["Service"] = "sns.amazonaws.com" },
                ["Action"] = "sqs:SendMessage",
                ["Resource"] = queueArn,
                ["Condition"] = new JsonObject { ["ArnEquals"] = new JsonObject { ["aws:SourceArn"] = topicArn } },
            }),
        };
        await sqs.SetQueueAttributesAsync(new SetQueueAttributesRequest { QueueUrl = queueUrl, Attributes = new() { ["Policy"] = policy.ToJsonString() } });

        var subArn = (await sns.SubscribeAsync(new SubscribeRequest
        {
            TopicArn = topicArn, Protocol = "sqs", Endpoint = queueArn,
            Attributes = new() { ["RawMessageDelivery"] = "true" }, ReturnSubscriptionArn = true,
        })).SubscriptionArn;
        ctx.OnCleanup(() => sns.UnsubscribeAsync(new UnsubscribeRequest { SubscriptionArn = subArn }));

        var deadline = FidelityTime.StartNew();
        var pending = (await sns.GetSubscriptionAttributesAsync(new GetSubscriptionAttributesRequest
        {
            SubscriptionArn = subArn,
        })).Attributes?.GetValueOrDefault("PendingConfirmation");
        while (pending != "false")
        {
            if (deadline.Elapsed >= TimeSpan.FromSeconds(60.0))
            {
                throw Fail($"subscription never left PendingConfirmation; last value {Py.Repr(pending)}");
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(1.0));
            pending = (await sns.GetSubscriptionAttributesAsync(new GetSubscriptionAttributesRequest
            {
                SubscriptionArn = subArn,
            })).Attributes?.GetValueOrDefault("PendingConfirmation");
        }

        var appName = FidelityContext.UniqueName("sim2real-behav-sns-appevt-app-");
        var appArn = (await sns.CreatePlatformApplicationAsync(new CreatePlatformApplicationRequest
        {
            Name = appName,
            Platform = "BAIDU",
            Attributes = new()
            {
                ["PlatformPrincipal"] = "sim2real-fake-api-key",
                ["PlatformCredential"] = "sim2real-fake-secret-key",
                ["EventEndpointCreated"] = topicArn,
            },
        })).PlatformApplicationArn;
        ctx.OnCleanup(() => sns.DeletePlatformApplicationAsync(new DeletePlatformApplicationRequest { PlatformApplicationArn = appArn }));

        var appAttrs = (await sns.GetPlatformApplicationAttributesAsync(new GetPlatformApplicationAttributesRequest
        {
            PlatformApplicationArn = appArn,
        })).Attributes ?? [];
        Ensure(appAttrs.GetValueOrDefault("EventEndpointCreated") == topicArn,
            "positive control: expected EventEndpointCreated to read back as the configured topic " +
            $"{Py.Repr(topicArn)}, got {Py.Repr(appAttrs.GetValueOrDefault("EventEndpointCreated"))}");

        var channelId = new BigInteger(Guid.NewGuid().ToByteArray(), isUnsigned: true).ToString(CultureInfo.InvariantCulture)[..16];
        var endpointArn = (await sns.CreatePlatformEndpointAsync(new CreatePlatformEndpointRequest
        {
            PlatformApplicationArn = appArn, Token = channelId,
            Attributes = new() { ["ChannelId"] = channelId, ["UserId"] = "sim2real-fake-baidu-user" },
        })).EndpointArn;
        ctx.OnCleanup(() => sns.DeleteEndpointAsync(new DeleteEndpointRequest { EndpointArn = endpointArn }));

        deadline = FidelityTime.StartNew();
        JsonObject? notification = null;
        while (notification is null)
        {
            var resp = await sqs.ReceiveMessageAsync(new ReceiveMessageRequest
            {
                QueueUrl = queueUrl, MaxNumberOfMessages = 10, WaitTimeSeconds = 5,
            });
            foreach (var msg in resp.Messages ?? [])
            {
                await sqs.DeleteMessageAsync(new DeleteMessageRequest { QueueUrl = queueUrl, ReceiptHandle = msg.ReceiptHandle });
                var parsed = JsonNode.Parse(msg.Body)!.AsObject();
                if (Str(parsed, "EndpointArn") == endpointArn)
                {
                    notification = parsed;
                }
            }

            if (notification is null && deadline.Elapsed >= TimeSpan.FromSeconds(90.0))
            {
                throw Fail($"no EventEndpointCreated notification naming {Py.Repr(endpointArn)} arrived within 90s");
            }
        }

        Ensure(Str(notification, "EventType") == "EndpointCreated",
            $"expected EventType 'EndpointCreated', got {Py.Repr(Str(notification, "EventType"))} " +
            $"in {notification.ToJsonString()}");
        Ensure(Str(notification, "EndpointArn") == endpointArn,
            $"expected EndpointArn {Py.Repr(endpointArn)}, got {Py.Repr(Str(notification, "EndpointArn"))}");
    }

    private static string? Str(JsonObject o, string key) =>
        o[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : o[key]?.ToJsonString();

    private static Func<Task> Teardown(Func<Task> undo, params string[] notFound) => async () =>
    {
        try
        {
            await undo();
        }
        catch (AmazonSQSException ex) when (notFound.Contains(ex.ErrorCode))
        {
        }
    };
}
