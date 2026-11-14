// Translated from behavioral/sns/check_publish_batch_to_a_deleted_topic_not_found.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Text.Json;
using System.Text.Json.Nodes;
using Amazon.SimpleNotificationService;
using Amazon.SimpleNotificationService.Model;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sns;

/// <summary>sns.publish-batch-to-a-deleted-topic-not-found</summary>
[Category("AwsEvaluationSuite")]
public sealed class PublishBatchToADeletedTopicNotFound
{
    private const string Scenario = "sns-publish-batch-deleted-topic";

    /// <summary>
    /// PublishBatch to a deleted topic is refused whole with NotFound and delivers nothing to its
    /// former queue, while a batch to a live topic subscribed by the same queue is delivered.
    /// </summary>
    [Test]
    [DisplayName("sns.publish-batch-to-a-deleted-topic-not-found")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    [KnownFidelityGap(FidelityGap.SnsErrorMessages, "PublishBatch to a deleted topic is refused with NotFound 'Topic not found: <arn>' instead of 'Topic does not exist'")]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sns = ctx.Sns;
        var doomed = await CreateTopic(ctx, "sim2real-behav-sns-doomed-");
        var live = await CreateTopic(ctx, "sim2real-behav-sns-live-");
        var (url, queueArn) = await CreateQueueForTopics(ctx, [doomed, live]);
        await SubscribeAndConfirm(ctx, doomed, queueArn);
        await SubscribeAndConfirm(ctx, live, queueArn);
        var runId = FidelityContext.UniqueName("sim2real-behav-sns-pbdel-");

        var before = $"{runId}-before-delete";
        var ok = await sns.PublishBatchAsync(new PublishBatchRequest
        {
            TopicArn = doomed,
            PublishBatchRequestEntries = [new() { Id = "before", Message = before }],
        });
        Ensure((ok.Successful ?? []).Select(e => e.Id).SequenceEqual(["before"]),
            $"PublishBatch to the live topic must succeed, got {ShowSuccessful(ok.Successful)} " +
            $"{ShowFailed(ok.Failed)}");
        await DrainUntil(ctx, url, [before]);

        await sns.DeleteTopicAsync(new DeleteTopicRequest { TopicArn = doomed });
        var refused = Enumerable.Range(0, 3).Select(i => $"{runId}-refused-{i}").ToList();
        var (code, message) = await Error(() => sns.PublishBatchAsync(new PublishBatchRequest
        {
            TopicArn = doomed,
            PublishBatchRequestEntries = refused.Select((body, i) => new PublishBatchRequestEntry { Id = $"e{i}", Message = body }).ToList(),
        }));
        Ensure(code == "NotFound" && message == "Topic does not exist",
            $"PublishBatch to a deleted topic: got '{code}' '{message}'");

        var after = Enumerable.Range(0, 2).Select(i => $"{runId}-sibling-{i}").ToList();
        var resp = await sns.PublishBatchAsync(new PublishBatchRequest
        {
            TopicArn = live,
            PublishBatchRequestEntries = after.Select((body, i) => new PublishBatchRequestEntry { Id = $"s{i}", Message = body }).ToList(),
        });
        Ensure((resp.Successful ?? []).Select(e => e.Id).Order(StringComparer.Ordinal).SequenceEqual(["s0", "s1"])
            && (resp.Failed ?? []).Count == 0,
            $"PublishBatch to the live sibling must succeed in full, got {ShowSuccessful(resp.Successful)} " +
            $"{ShowFailed(resp.Failed)}");
        var seen = await DrainUntil(ctx, url, after.ToHashSet());
        var leaked = refused.Intersect(seen.Keys).Order(StringComparer.Ordinal).ToList();
        Ensure(leaked.Count == 0, $"entries of the refused batch were delivered: {Show(leaked)}");
        foreach (var body in after)
        {
            Ensure((string?)JsonNode.Parse(seen[body].Body)!["TopicArn"] == live,
                $"'{body}' must arrive from the live topic");
        }
    }

    private static string Show(IEnumerable<string> values) => "[" + string.Join(", ", values.Select(v => $"'{v}'")) + "]";

    private static string ShowSuccessful(List<PublishBatchResultEntry>? entries) => entries is null ? "None" :
        "[" + string.Join(", ", entries.Select(e => $"{{'Id': '{e.Id}', 'MessageId': '{e.MessageId}'}}")) + "]";

    private static string ShowFailed(List<Amazon.SimpleNotificationService.Model.BatchResultErrorEntry>? entries) => entries is null ? "None" :
        "[" + string.Join(", ", entries.Select(e => $"{{'Id': '{e.Id}', 'Code': '{e.Code}', 'Message': '{e.Message}'}}")) + "]";

    /// <summary>The (Code, Message) of the ClientError fn raises. An unexpected success raises too.</summary>
    private static async Task<(string? Code, string Message)> Error(Func<Task<PublishBatchResponse>> fn)
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is null)
        {
            throw Fail($"expected the call to be refused, but it succeeded: {{'Successful': {ShowSuccessful(result!.Successful)}, 'Failed': {ShowFailed(result.Failed)}}}");
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
                    ["ArnEquals"] = new JsonObject { ["aws:SourceArn"] = new JsonArray(topicArns.Select(t => (JsonNode)JsonValue.Create(t)).ToArray()) },
                },
            }),
        };
        await sqs.SetQueueAttributesAsync(new SetQueueAttributesRequest { QueueUrl = url, Attributes = new() { ["Policy"] = policy.ToJsonString() } });
        return (url, arn);
    }

    /// <summary>Subscribe an SQS queue, register Unsubscribe, poll until PendingConfirmation is false.</summary>
    private static async Task<string> SubscribeAndConfirm(FidelityContext ctx, string topicArn, string queueArn, double timeoutS = 60.0)
    {
        var sns = ctx.Sns;
        var subArn = (await sns.SubscribeAsync(new SubscribeRequest { TopicArn = topicArn, Protocol = "sqs", Endpoint = queueArn, ReturnSubscriptionArn = true })).SubscriptionArn;
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
            return (string?)obj["Message"] ?? "";
        }

        return body;
    }

    /// <summary>
    /// Receive (and delete) until every key in wanted has been seen; return key -> message
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
                QueueUrl = url,
                MaxNumberOfMessages = 10,
                WaitTimeSeconds = 5,
                MessageAttributeNames = ["All"],
                MessageSystemAttributeNames = ["All"],
            })).Messages ?? [])
            {
                await sqs.DeleteMessageAsync(new DeleteMessageRequest { QueueUrl = url, ReceiptHandle = msg.ReceiptHandle });
                seen[Key(msg)] = msg;
            }
        }

        var missing = wanted.Except(seen.Keys).Order(StringComparer.Ordinal).ToList();
        Ensure(missing.Count == 0, $"within {timeoutS}s the queue never received {Show(missing)}; it received {Show(seen.Keys.Order(StringComparer.Ordinal))}");
        return seen;
    }
}
