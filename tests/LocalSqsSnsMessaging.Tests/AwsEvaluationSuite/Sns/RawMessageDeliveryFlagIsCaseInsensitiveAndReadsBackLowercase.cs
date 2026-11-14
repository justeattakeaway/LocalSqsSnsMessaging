// Translated from behavioral/sns/check_raw_message_delivery_flag_is_case_insensitive_and_reads_back_lowercase.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Text.Json;
using System.Text.Json.Nodes;
using Amazon.SimpleNotificationService;
using Amazon.SimpleNotificationService.Model;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sns;

/// <summary>sns.raw-message-delivery-flag-is-case-insensitive-and-reads-back-lowercase</summary>
[Category("AwsEvaluationSuite")]
public sealed class RawMessageDeliveryFlagIsCaseInsensitiveAndReadsBackLowercase
{
    private const string Scenario = "sns-raw-delivery-case";

    /// <summary>
    /// RawMessageDelivery "True"/"FALSE" read back lowercase and switch delivery to raw and back
    /// to the envelope; a non-boolean "yes" is refused with InvalidParameter, leaving "false" stored.
    /// </summary>
    [Test]
    [DisplayName("sns.raw-message-delivery-flag-is-case-insensitive-and-reads-back-lowercase")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    [KnownFidelityGap(FidelityGap.SnsSubscriptionAttributes, "RawMessageDelivery 'True' reads back as 'True' instead of 'true'")]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sns = ctx.Sns;
        var topic = await CreateTopic(ctx);
        var (url, queueArn) = await CreateQueueForTopics(ctx, [topic]);
        var sub = await SubscribeAndConfirm(ctx, topic, queueArn, new() { ["RawMessageDelivery"] = "True" });

        async Task<string?> Stored() =>
            (await sns.GetSubscriptionAttributesAsync(new GetSubscriptionAttributesRequest { SubscriptionArn = sub }))
                .Attributes?.GetValueOrDefault("RawMessageDelivery");

        Ensure(await Stored() == "true", $"RawMessageDelivery 'True' must read back as 'true', got '{await Stored()}'");

        var marker = FidelityContext.UniqueName("sim2real-behav-sns-rawcase-on-");
        await sns.PublishAsync(new PublishRequest { TopicArn = topic, Message = marker });
        var body = (await DrainUntil(ctx, url, [marker]))[marker].Body;
        Ensure(body == marker, $"'True' must enable raw delivery; the queue received '{Head(body, 160)}'");

        await sns.SetSubscriptionAttributesAsync(new SetSubscriptionAttributesRequest
        {
            SubscriptionArn = sub, AttributeName = "RawMessageDelivery", AttributeValue = "FALSE",
        });
        Ensure(await Stored() == "false", $"'FALSE' must read back as 'false', got '{await Stored()}'");

        var (code, message) = await Error(() => sns.SetSubscriptionAttributesAsync(new SetSubscriptionAttributesRequest
        {
            SubscriptionArn = sub, AttributeName = "RawMessageDelivery", AttributeValue = "yes",
        }));
        Ensure((code, message) == ("InvalidParameter", "Invalid parameter: RawMessageDelivery: Invalid value [yes]. Must be true or false."),
            $"RawMessageDelivery 'yes': got '{code}' '{message}'");
        Ensure(await Stored() == "false", $"the refused value changed the stored flag to '{await Stored()}'");

        // The switch back to the envelope is eventually consistent; allow a few raw deliveries.
        var formats = new List<string>();
        for (var i = 0; i < 6; i++)
        {
            marker = FidelityContext.UniqueName("sim2real-behav-sns-rawcase-off-");
            await sns.PublishAsync(new PublishRequest { TopicArn = topic, Message = marker });
            body = (await DrainUntil(ctx, url, [marker]))[marker].Body;
            if (body != marker)
            {
                var envelope = JsonNode.Parse(body)!.AsObject();
                Ensure(((string?)envelope["Type"], (string?)envelope["Message"]) == ("Notification", marker),
                    $"after 'FALSE' the queue must receive the envelope, got '{Head(body, 160)}'");
                return;
            }

            formats.Add("raw");
        }

        throw Fail($"after RawMessageDelivery 'FALSE', 6 deliveries were all [{string.Join(", ", formats.Select(f => $"'{f}'"))}]");
    }

    private static string Head(string s, int n) => s.Length <= n ? s : s[..n];

    /// <summary>The (Code, Message) of the ClientError <c>fn</c> raises. An unexpected success raises too.</summary>
    private static async Task<(string? Code, string Message)> Error<T>(Func<Task<T>> fn)
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
                    ["ArnEquals"] = new JsonObject { ["aws:SourceArn"] = new JsonArray([.. topicArns.Select(t => (JsonNode?)t)]) },
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

        if (env is JsonObject obj && obj["Type"] is JsonValue type && type.GetValueKind() == JsonValueKind.String && type.GetValue<string>() == "Notification")
        {
            return obj["Message"] switch
            {
                null => "",
                JsonValue m when m.GetValueKind() == JsonValueKind.String => m.GetValue<string>(),
                var m => m.ToJsonString(),
            };
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
            foreach (var msg in (await sqs.ReceiveMessageAsync(new ReceiveMessageRequest
                     {
                         QueueUrl = url, MaxNumberOfMessages = 10, WaitTimeSeconds = 5,
                         MessageAttributeNames = ["All"], MessageSystemAttributeNames = ["All"],
                     })).Messages ?? [])
            {
                await sqs.DeleteMessageAsync(new DeleteMessageRequest { QueueUrl = url, ReceiptHandle = msg.ReceiptHandle });
                seen[Key(msg)] = msg;
            }
        }

        var missing = wanted.Except(seen.Keys).Order(StringComparer.Ordinal).ToList();
        Ensure(missing.Count == 0,
            $"within {timeoutS}s the queue never received [{string.Join(", ", missing.Select(m => $"'{m}'"))}]; " +
            $"it received [{string.Join(", ", seen.Keys.Order(StringComparer.Ordinal).Select(k => $"'{k}'"))}]");
        return seen;
    }
}
