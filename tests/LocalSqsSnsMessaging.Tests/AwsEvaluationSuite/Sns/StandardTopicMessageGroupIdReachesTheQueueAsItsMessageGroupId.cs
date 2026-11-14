// Translated from behavioral/sns/check_standard_topic_message_group_id_reaches_the_queue_as_its_message_group_id.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Text.Json;
using System.Text.Json.Nodes;
using Amazon.SimpleNotificationService;
using Amazon.SimpleNotificationService.Model;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sns;

/// <summary>sns.standard-topic-message-group-id-reaches-the-queue-as-its-message-group-id</summary>
[Category("AwsEvaluationSuite")]
public sealed class StandardTopicMessageGroupIdReachesTheQueueAsItsMessageGroupId
{
    private const string Scenario = "sns-standard-topic-message-group";

    /// <summary>
    /// A standard topic forwards a published MessageGroupId (Publish and PublishBatch, raw and
    /// enveloped) as the SQS MessageGroupId system attribute, omits it when none was published,
    /// and returns no SequenceNumber. This is how SNS feeds SQS fair queues.
    /// </summary>
    [Test]
    [DisplayName("sns.standard-topic-message-group-id-reaches-the-queue-as-its-message-group-id")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    [KnownFidelityGap(FidelityGap.SnsFifo, "A MessageGroupId published to a standard topic is not forwarded as the SQS MessageGroupId system attribute (raw or enveloped)")]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sns = ctx.Sns;
        var topic = await CreateTopic(ctx);
        var (rawUrl, rawArn) = await CreateQueueForTopics(ctx, [topic]);
        var (envUrl, envArn) = await CreateQueueForTopics(ctx, [topic]);
        await SubscribeAndConfirm(ctx, topic, rawArn, new() { ["RawMessageDelivery"] = "true" });
        await SubscribeAndConfirm(ctx, topic, envArn);

        var runId = FidelityContext.UniqueName("sim2real-behav-sns-fairgrp-");
        var expected = new Dictionary<string, string?>
        {
            [$"{runId}-single-grouped"] = "tenant-a",
            [$"{runId}-single-plain"] = null,
            [$"{runId}-batch-grouped"] = "tenant-b",
            [$"{runId}-batch-plain"] = null,
        };

        var grouped = await sns.PublishAsync(new PublishRequest { TopicArn = topic, Message = $"{runId}-single-grouped", MessageGroupId = "tenant-a" });
        var plain = await sns.PublishAsync(new PublishRequest { TopicArn = topic, Message = $"{runId}-single-plain" });
        foreach (var resp in new[] { grouped, plain })
        {
            Ensure(!string.IsNullOrEmpty(resp.MessageId) && resp.SequenceNumber is null,
                "a standard-topic Publish returns a MessageId and no SequenceNumber, got " +
                $"{{'MessageId': '{resp.MessageId}'{(resp.SequenceNumber is null ? "" : $", 'SequenceNumber': '{resp.SequenceNumber}'")}}}");
        }

        var batch = await sns.PublishBatchAsync(new PublishBatchRequest
        {
            TopicArn = topic,
            PublishBatchRequestEntries =
            [
                new() { Id = "grouped", Message = $"{runId}-batch-grouped", MessageGroupId = "tenant-b" },
                new() { Id = "plain", Message = $"{runId}-batch-plain" },
            ],
        });
        Ensure((batch.Successful ?? []).Select(e => e.Id).Order(StringComparer.Ordinal).SequenceEqual(["grouped", "plain"])
            && batch.Failed is null or { Count: 0 },
            $"both PublishBatch entries must succeed, got Successful=[{string.Join(", ", (batch.Successful ?? []).Select(e => $"{{'Id': '{e.Id}'}}"))}] " +
            $"Failed=[{string.Join(", ", (batch.Failed ?? []).Select(f => $"{{'Id': '{f.Id}', 'Code': '{f.Code}'}}"))}]");

        foreach (var (label, url) in new[] { ("raw", rawUrl), ("enveloped", envUrl) })
        {
            var seen = await DrainUntil(ctx, url, expected.Keys.ToHashSet());
            var got = expected.Keys.ToDictionary(k => k, k => seen[k].Attributes?.GetValueOrDefault("MessageGroupId"));
            Ensure(got.All(kv => kv.Value == expected[kv.Key]),
                $"on the {label} subscription the MessageGroupId each message carried was {Repr(got)}, " +
                $"expected {Repr(expected)}");
            var extra = seen.Keys.Except(expected.Keys).Order(StringComparer.Ordinal).ToList();
            Ensure(extra.Count == 0, $"the {label} queue received messages nobody published: [{string.Join(", ", extra.Select(e => $"'{e}'"))}]");
        }
    }

    private static string Repr(Dictionary<string, string?> d) =>
        "{" + string.Join(", ", d.Select(kv => $"'{kv.Key}': {(kv.Value is null ? "None" : $"'{kv.Value}'")}")) + "}";

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
                    ["ArnEquals"] = new JsonObject { ["aws:SourceArn"] = new JsonArray(topicArns.Select(t => (JsonNode?)JsonValue.Create(t)).ToArray()) },
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

        if (env is JsonObject obj && obj["Type"] is JsonValue type && type.TryGetValue<string>(out var t) && t == "Notification")
        {
            return obj["Message"]?.ToString() ?? "";
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
        var seen = new Dictionary<string, Message>(StringComparer.Ordinal);
        var deadline = FidelityTime.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(timeoutS) && !wanted.IsSubsetOf(seen.Keys))
        {
            var resp = await sqs.ReceiveMessageAsync(new ReceiveMessageRequest
            {
                QueueUrl = url, MaxNumberOfMessages = 10, WaitTimeSeconds = 5,
                MessageAttributeNames = ["All"], MessageSystemAttributeNames = ["All"],
            });
            foreach (var msg in resp.Messages ?? [])
            {
                await sqs.DeleteMessageAsync(new DeleteMessageRequest { QueueUrl = url, ReceiptHandle = msg.ReceiptHandle });
                seen[Key(msg)] = msg;
            }
        }

        var missing = wanted.Except(seen.Keys).Order(StringComparer.Ordinal).ToList();
        Ensure(missing.Count == 0,
            $"within {timeoutS}s the queue never received [{string.Join(", ", missing.Select(m => $"'{m}'"))}]; it received [{string.Join(", ", seen.Keys.Order(StringComparer.Ordinal).Select(m => $"'{m}'"))}]");
        return seen;
    }
}
