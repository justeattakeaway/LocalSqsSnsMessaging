// Translated from behavioral/sns/check_fifo_topic_preserves_publish_order_and_deduplicates_by_id.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Text.Json;
using System.Text.Json.Nodes;
using Amazon.SimpleNotificationService.Model;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sns;

/// <summary>sns.fifo-topic-preserves-publish-order-and-deduplicates-by-id</summary>
[Category("AwsEvaluationSuite")]
public sealed class FifoTopicPreservesPublishOrderAndDeduplicatesById
{
    private const string Scenario = "sns-fifo-ordering";

    /// <summary>
    /// Ten messages published sequentially into one MessageGroupId of a FIFO topic arrive at the
    /// FIFO queue in exactly that order, and five concurrent publishes sharing one
    /// MessageDeduplicationId yield exactly one delivered message.
    /// </summary>
    [Test]
    [DisplayName("sns.fifo-topic-preserves-publish-order-and-deduplicates-by-id")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sns = ctx.Sns;
        var topicArn = await CreateTopic(ctx, fifo: true, attributes: new() { ["ContentBasedDeduplication"] = "false" });
        var (queueUrl, queueArn) = await CreateQueueForTopic(ctx, topicArn, fifo: true);
        await SubscribeAndConfirm(ctx, topicArn, queueArn);

        var runId = FidelityContext.UniqueName("sim2real-behav-sns-fifo-");
        var group = $"{runId}-order";
        var sent = Enumerable.Range(0, 10).Select(i => $"{runId}-{i:00}").ToList();
        for (var i = 0; i < sent.Count; i++)
        {
            await sns.PublishAsync(new PublishRequest
            {
                TopicArn = topicArn,
                Message = sent[i],
                MessageGroupId = group,
                MessageDeduplicationId = $"{runId}-dedup-{i:00}",
            });
        }

        var received = await DrainMessages(ctx, queueUrl, want: 10, timeoutS: 120.0);
        Ensure(received.Count == 10, $"the FIFO queue did not receive every message: {Repr(received)}");
        Ensure(received.SequenceEqual(sent), $"FIFO order was not preserved: {Repr(received)} != {Repr(sent)}");

        var dedupGroup = $"{runId}-dedup-group";
        var dedupId = $"{runId}-shared-dedup";
        var dedupBodies = Enumerable.Range(0, 5).Select(i => $"{runId}-dup-{i}").ToList();

        async Task<string?> Publish(string body)
        {
            try
            {
                await sns.PublishAsync(new PublishRequest { TopicArn = topicArn, Message = body, MessageGroupId = dedupGroup, MessageDeduplicationId = dedupId });
                return null;
            }
#pragma warning disable CA1031 // captured so the assertion can name it
            catch (Exception exc)
#pragma warning restore CA1031
            {
                return $"{body}: {exc.GetType().Name}: {exc.Message}";
            }
        }

        var dedupErrors = (await Task.WhenAll(dedupBodies.Select(b => Task.Run(() => Publish(b)))))
            .Where(e => e is not null).ToList();
        Ensure(dedupErrors.Count == 0, $"a deduplicated publish failed instead of being collapsed: {Repr(dedupErrors)}");

        // FIFO order within the group makes the sentinel arrive last, bounding the drain.
        var sentinel = $"{runId}-sentinel";
        await sns.PublishAsync(new PublishRequest
        {
            TopicArn = topicArn,
            Message = sentinel,
            MessageGroupId = dedupGroup,
            MessageDeduplicationId = $"{runId}-sentinel-dedup",
        });
        var after = await DrainUntilBody(ctx, queueUrl, sentinel, timeoutS: 120.0);
        var deliveredDuplicates = after.Where(dedupBodies.Contains).ToList();
        var dedupCount = deliveredDuplicates.Count;
        Ensure(dedupCount == 1,
            $"a shared MessageDeduplicationId produced {dedupCount} deliveries, expected exactly 1: " +
            $"{Repr(deliveredDuplicates)} (everything seen before the sentinel was {Repr(after)})");
    }

    private static string Repr(IEnumerable<string?> items) => "[" + string.Join(", ", items.Select(s => $"'{s}'")) + "]";

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
        if (attributes is { Count: > 0 })
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

    /// <summary>Unwrap the SNS Notification envelope and return its Message, or the body if it is not one.</summary>
    private static string InnerMessage(string body)
    {
        JsonNode? envelope;
        try
        {
            envelope = JsonNode.Parse(body);
        }
        catch (JsonException)
        {
            return body;
        }

        if (envelope is JsonObject obj && obj["Type"] is JsonValue type && type.TryGetValue<string>(out var t) && t == "Notification"
            && obj["Message"] is JsonValue inner && inner.TryGetValue<string>(out var innerText))
        {
            return innerText;
        }

        return body;
    }

    /// <summary>Receive until `want` messages arrive; returns the inner Messages in arrival order.</summary>
    private static async Task<List<string>> DrainMessages(FidelityContext ctx, string queueUrl, int want, double timeoutS = 120.0)
    {
        var sqs = ctx.Sqs;
        var collected = new List<string>();
        var deadline = FidelityTime.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(timeoutS) && collected.Count < want)
        {
            var resp = await sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = queueUrl, MaxNumberOfMessages = 10, WaitTimeSeconds = 5 });
            foreach (var msg in resp.Messages ?? [])
            {
                await sqs.DeleteMessageAsync(new DeleteMessageRequest { QueueUrl = queueUrl, ReceiptHandle = msg.ReceiptHandle });
                collected.Add(InnerMessage(msg.Body ?? ""));
            }
        }

        return collected;
    }

    /// <summary>Receive until the sentinel body arrives; returns everything seen BEFORE it, in order.</summary>
    private static async Task<List<string>> DrainUntilBody(FidelityContext ctx, string queueUrl, string sentinel, double timeoutS = 120.0)
    {
        var sqs = ctx.Sqs;
        var before = new List<string>();
        var deadline = FidelityTime.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(timeoutS))
        {
            var resp = await sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = queueUrl, MaxNumberOfMessages = 10, WaitTimeSeconds = 5 });
            foreach (var msg in resp.Messages ?? [])
            {
                await sqs.DeleteMessageAsync(new DeleteMessageRequest { QueueUrl = queueUrl, ReceiptHandle = msg.ReceiptHandle });
                var inner = InnerMessage(msg.Body ?? "");
                if (inner == sentinel)
                {
                    return before;
                }

                before.Add(inner);
            }
        }

        throw Fail($"the FIFO sentinel '{sentinel}' never arrived within {timeoutS}s, so the deduplication " +
            $"count is not bounded; messages seen were {Repr(before)}");
    }
}
