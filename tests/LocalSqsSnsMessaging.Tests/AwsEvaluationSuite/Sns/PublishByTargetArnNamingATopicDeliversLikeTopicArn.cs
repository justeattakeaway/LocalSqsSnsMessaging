// Translated from behavioral/sns/check_publish_by_target_arn_naming_a_topic_delivers_like_topic_arn.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Text.Json;
using System.Text.Json.Nodes;
using Amazon.SimpleNotificationService;
using Amazon.SimpleNotificationService.Model;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sns;

/// <summary>sns.publish-by-target-arn-naming-a-topic-delivers-like-topic-arn</summary>
[Category("AwsEvaluationSuite")]
public sealed class PublishByTargetArnNamingATopicDeliversLikeTopicArn
{
    private const string Scenario = "sns-publish-target-arn";

    /// <summary>
    /// Publish with TargetArn naming a topic delivers a Notification from that topic with the
    /// returned MessageId; naming both TopicArn and TargetArn is refused and never delivered.
    /// </summary>
    [Test]
    [DisplayName("sns.publish-by-target-arn-naming-a-topic-delivers-like-topic-arn")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    [KnownFidelityGap(FidelityGap.SnsValidation, "Publish with TargetArn (a topic ARN) instead of TopicArn is refused with 'Invalid parameter: TopicArn'")]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sns = ctx.Sns;
        var topic = await CreateTopic(ctx);
        var (url, queueArn) = await CreateQueueForTopics(ctx, [topic]);
        await SubscribeAndConfirm(ctx, topic, queueArn);

        var runId = FidelityContext.UniqueName("sim2real-behav-sns-target-");
        var byTarget = $"{runId}-by-target-arn";
        var both = $"{runId}-both-arns";
        var sentinel = $"{runId}-sentinel";

        var resp = await sns.PublishAsync(new PublishRequest { TargetArn = topic, Message = byTarget });
        var targetId = resp.MessageId;
        Ensure(!string.IsNullOrEmpty(targetId), $"Publish(TargetArn=<topic>) must return a MessageId, got {resp}");

        var (code, message) = await Error(() => sns.PublishAsync(new PublishRequest { TopicArn = topic, TargetArn = topic, Message = both }));
        Ensure(code == "InvalidParameter", $"Publish naming both TopicArn and TargetArn must be InvalidParameter, got '{code}'");
        Ensure(message == "Invalid parameter: TopicArn Reason: Both TopicArn and TargetArn specified. Use only one or the other.",
            $"unexpected message for a publish naming both targets: '{message}'");

        await sns.PublishAsync(new PublishRequest { TopicArn = topic, Message = sentinel });
        var seen = await DrainUntil(ctx, url, [byTarget, sentinel]);
        var envelope = JsonNode.Parse(seen[byTarget].Body)!.AsObject();
        var (type, topicArn, messageId) = ((string?)envelope["Type"], (string?)envelope["TopicArn"], (string?)envelope["MessageId"]);
        Ensure((type, topicArn, messageId) == ("Notification", topic, targetId),
            "the TargetArn publish must arrive as a Notification from the topic carrying the " +
            $"returned MessageId; got Type='{type}', TopicArn matches=" +
            $"{(topicArn == topic ? "True" : "False")}, MessageId matches=" +
            $"{(messageId == targetId ? "True" : "False")}");
        Ensure(!seen.ContainsKey(both), "the publish refused for naming both targets was delivered anyway");
        Ensure(seen.Keys.ToHashSet().SetEquals([byTarget, sentinel]),
            $"the queue received unexpected messages: [{string.Join(", ", seen.Keys.Except([byTarget, sentinel]).Order(StringComparer.Ordinal).Select(k => $"'{k}'"))}]");
    }

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
