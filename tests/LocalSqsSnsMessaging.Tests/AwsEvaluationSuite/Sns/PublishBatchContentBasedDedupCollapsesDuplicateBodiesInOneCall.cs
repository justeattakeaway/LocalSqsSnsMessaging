// Translated from behavioral/sns/check_publish_batch_content_based_dedup_collapses_duplicate_bodies_in_one_call.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Text.Json.Nodes;
using Amazon.SimpleNotificationService.Model;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sns;

/// <summary>sns.publish-batch-content-based-dedup-collapses-duplicate-bodies-in-one-call</summary>
[Category("AwsEvaluationSuite")]
public sealed class PublishBatchContentBasedDedupCollapsesDuplicateBodiesInOneCall
{
    private const string Scenario = "sns-batch-content-dedup";

    /// <summary>
    /// With ContentBasedDeduplication on a FIFO topic, two same-group entries with identical
    /// bodies in one PublishBatch both report Successful but deliver one copy; a distinct entry
    /// in the same call is delivered once.
    /// </summary>
    [Test]
    [DisplayName("sns.publish-batch-content-based-dedup-collapses-duplicate-bodies-in-one-call")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sns = ctx.Sns;
        var topicArn = await CreateTopic(ctx, attributes: new() { ["ContentBasedDeduplication"] = "true" });
        var (queueUrl, queueArn) = await CreateQueueForTopic(ctx, topicArn);
        await SubscribeAndConfirm(ctx, topicArn, queueArn, attributes: new() { ["RawMessageDelivery"] = "true" });

        var runId = FidelityContext.UniqueName("sim2real-behav-sns-cbd-");
        var group = $"{runId}-group";
        var duplicatedBody = $"{runId}-duplicated";
        var distinctBody = $"{runId}-distinct";
        var sentinelBody = $"{runId}-sentinel";

        var resp = await sns.PublishBatchAsync(new PublishBatchRequest
        {
            TopicArn = topicArn,
            PublishBatchRequestEntries =
            [
                new() { Id = "dup-a", Message = duplicatedBody, MessageGroupId = group },
                new() { Id = "dup-b", Message = duplicatedBody, MessageGroupId = group },
                new() { Id = "distinct", Message = distinctBody, MessageGroupId = group },
            ],
        });
        Ensure(resp.Failed is null or { Count: 0 },
            $"the batch reported failures: [{string.Join(", ", (resp.Failed ?? []).Select(f => $"{{'Id': '{f.Id}', 'Code': '{f.Code}', 'Message': '{f.Message}', 'SenderFault': {f.SenderFault}}}"))}]");
        var successfulIds = (resp.Successful ?? []).Select(e => e.Id).Order(StringComparer.Ordinal).ToList();
        Ensure(successfulIds.SequenceEqual(["distinct", "dup-a", "dup-b"]),
            "content-based dedup is silent -- both duplicate entries must still report Successful, " +
            $"got [{string.Join(", ", (resp.Successful ?? []).Select(e => $"{{'Id': '{e.Id}', 'MessageId': '{e.MessageId}'}}"))}]");

        // The sentinel, published after and into the same group, bounds the read: once it has
        // arrived, nothing ahead of it in group order is still in flight.
        await sns.PublishAsync(new PublishRequest { TopicArn = topicArn, Message = sentinelBody, MessageGroupId = group });
        var beforeSentinel = await DrainUntilBody(ctx, queueUrl, sentinelBody, timeoutS: 90.0);

        var dupCount = beforeSentinel.Count(b => b == duplicatedBody);
        Ensure(dupCount == 1,
            "two batch entries with an identical body and ContentBasedDeduplication enabled must " +
            $"deliver exactly one copy, got {dupCount} copies; messages seen before the sentinel: " +
            $"{PyList(beforeSentinel)}");
        Ensure(beforeSentinel.Count(b => b == distinctBody) == 1,
            "the distinct-body entry in the same batch must be delivered exactly once, got " +
            $"{beforeSentinel.Count(b => b == distinctBody)}; messages seen: {PyList(beforeSentinel)}");
    }

    private static string PyList(IEnumerable<string> values) => $"[{string.Join(", ", values.Select(v => $"'{v}'"))}]";

    private static List<Amazon.SimpleNotificationService.Model.Tag> TagsKv() =>
        [new() { Key = "sim2real", Value = Scenario }, new() { Key = "sim2real:managed", Value = "true" }];

    /// <summary>CreateTopic (FIFO, tagged + prefix-named) and register DeleteTopic immediately.</summary>
    private static async Task<string> CreateTopic(FidelityContext ctx, Dictionary<string, string>? attributes = null)
    {
        var name = FidelityContext.UniqueName("sim2real-behav-sns-cbd-") + ".fifo";
        var attrs = new Dictionary<string, string>(attributes ?? []) { ["FifoTopic"] = "true" };
        var arn = (await ctx.Sns.CreateTopicAsync(new CreateTopicRequest { Name = name, Attributes = attrs, Tags = TagsKv() })).TopicArn;
        ctx.OnCleanup(() => ctx.Sns.DeleteTopicAsync(new DeleteTopicRequest { TopicArn = arn }));
        return arn;
    }

    /// <summary>Create a real FIFO SQS queue whose policy lets exactly this topic send to it.</summary>
    private static async Task<(string Url, string Arn)> CreateQueueForTopic(FidelityContext ctx, string topicArn)
    {
        var sqs = ctx.Sqs;
        var name = FidelityContext.UniqueName("sim2real-behav-sns-cbd-q-") + ".fifo";
        var url = (await sqs.CreateQueueAsync(new CreateQueueRequest
        {
            QueueName = name,
            Attributes = new() { ["FifoQueue"] = "true" },
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

    /// <summary>Receive until the sentinel body arrives; returns everything seen BEFORE it, in order.</summary>
    private static async Task<List<string>> DrainUntilBody(FidelityContext ctx, string queueUrl, string sentinel, double timeoutS = 90.0)
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
                var body = msg.Body ?? "";
                if (body == sentinel)
                {
                    return before;
                }

                before.Add(body);
            }
        }

        throw Fail($"the sentinel '{sentinel}' never arrived within {timeoutS}s; saw {PyList(before)}");
    }
}
