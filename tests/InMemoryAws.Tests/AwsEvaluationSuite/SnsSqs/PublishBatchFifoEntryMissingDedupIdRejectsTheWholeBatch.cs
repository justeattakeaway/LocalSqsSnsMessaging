// Translated from behavioral/sns__sqs/check_publish_batch_fifo_entry_missing_dedup_id_rejects_the_whole_batch.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit abd0811).
using System.Text.Json.Nodes;
using Amazon.Runtime;
using Amazon.SimpleNotificationService.Model;
using Amazon.SQS.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.SnsSqs;

/// <summary>sns__sqs.publish-batch-fifo-entry-missing-dedup-id-rejects-the-whole-batch</summary>
[Category("AwsEvaluationSuite")]
public sealed class PublishBatchFifoEntryMissingDedupIdRejectsTheWholeBatch
{
    private const string Scenario = "sns-batch-nodedup";

    /// <summary>
    /// On a FIFO topic without ContentBasedDeduplication, a PublishBatch with one entry missing
    /// MessageDeduplicationId is rejected whole with InvalidParameter and delivers no entry, while
    /// the same batch with every entry's dedup id present is accepted and delivered.
    /// </summary>
    [Test]
    [DisplayName("sns__sqs.publish-batch-fifo-entry-missing-dedup-id-rejects-the-whole-batch")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sns = ctx.Sns;
        var topicArn = await CreateTopic(ctx);
        var (queueUrl, queueArn) = await CreateQueueForTopic(ctx, topicArn);
        await SubscribeAndConfirm(ctx, topicArn, queueArn);

        var runId = FidelityContext.UniqueName("sim2real-behav-sns-batchnodedup-");
        var group = $"{runId}-group";
        var goodMarker = $"{runId}-good";
        var brokenMarker = $"{runId}-broken";

        var (code, message) = await Error(() => sns.PublishBatchAsync(new PublishBatchRequest
        {
            TopicArn = topicArn,
            PublishBatchRequestEntries =
            [
                new PublishBatchRequestEntry { Id = "good", Message = goodMarker, MessageGroupId = group, MessageDeduplicationId = $"{runId}-dedup-good" },
                new PublishBatchRequestEntry { Id = "broken", Message = brokenMarker, MessageGroupId = group },
            ],
        }));
        Ensure(code == "InvalidParameter",
            "expected InvalidParameter for a PublishBatch with one FIFO entry missing " +
            $"MessageDeduplicationId on a non-content-based-dedup topic, got {code}");
        const string expected = "Invalid parameter: The topic should either have ContentBasedDeduplication enabled " +
            "or MessageDeduplicationId provided explicitly";
        Ensure(message == expected, $"expected message {Py.Repr(expected)}, got {Py.Repr(message)}");

        var sentinel = $"{runId}-sentinel";
        await sns.PublishAsync(new PublishRequest { TopicArn = topicArn, Message = sentinel, MessageGroupId = group, MessageDeduplicationId = $"{runId}-dedup-sentinel" });
        var before = await DrainUntilBody(ctx, queueUrl, sentinel, timeoutS: 120.0);
        Ensure(before.Count == 0,
            $"the batch was rejected as a whole, so neither entry may be delivered, but {Repr(before)} arrived before the sentinel");

        var resp = await sns.PublishBatchAsync(new PublishBatchRequest
        {
            TopicArn = topicArn,
            PublishBatchRequestEntries =
            [
                new PublishBatchRequestEntry { Id = "good", Message = goodMarker, MessageGroupId = group, MessageDeduplicationId = $"{runId}-dedup-good2" },
                new PublishBatchRequestEntry { Id = "broken", Message = brokenMarker, MessageGroupId = group, MessageDeduplicationId = $"{runId}-dedup-broken2" },
            ],
        });
        // The .NET SDK may leave an empty Failed list null where boto3 returns [].
        Ensure(resp.Failed is null or { Count: 0 },
            $"expected no Failed entries, got [{string.Join(", ", (resp.Failed ?? []).Select(f => $"{f.Id}: {f.Code} {f.Message}"))}]");
        var successfulIds = (resp.Successful ?? []).Select(e => e.Id).ToHashSet();
        Ensure(successfulIds.SetEquals(["good", "broken"]),
            $"expected both repaired entries in Successful, got [{string.Join(", ", successfulIds)}]");

        var sentinel2 = $"{runId}-sentinel2";
        await sns.PublishAsync(new PublishRequest { TopicArn = topicArn, Message = sentinel2, MessageGroupId = group, MessageDeduplicationId = $"{runId}-dedup-sentinel2" });
        var delivered = await DrainUntilBody(ctx, queueUrl, sentinel2, timeoutS: 120.0);
        Ensure(delivered.Order(StringComparer.Ordinal).SequenceEqual(new[] { goodMarker, brokenMarker }.Order(StringComparer.Ordinal)),
            $"expected both repaired entries to have been delivered, got {Repr(delivered)}");
    }

    private static string Repr(IEnumerable<string> items) => "[" + string.Join(", ", items.Select(i => $"'{i}'")) + "]";

    /// <summary>A cleanup running <paramref name="undo"/> where a failed delete raises, unless its code is one of <paramref name="notFound"/>.</summary>
    private static Func<Task> Teardown(Func<Task> undo, params string[] notFound) => async () =>
    {
        try
        {
            await undo();
        }
        catch (AmazonServiceException exc) when (notFound.Contains(exc.ErrorCode))
        {
        }
    };

    private static List<Amazon.SimpleNotificationService.Model.Tag> TagsKv() =>
        [new() { Key = "sim2real", Value = Scenario }, new() { Key = "sim2real:managed", Value = "true" }];

    /// <summary>Create a FIFO topic without content-based dedup and register its deletion.</summary>
    private static async Task<string> CreateTopic(FidelityContext ctx)
    {
        var name = FidelityContext.UniqueName("sim2real-behav-sns-batchnodedup-") + ".fifo";
        var arn = (await ctx.Sns.CreateTopicAsync(new CreateTopicRequest
        {
            Name = name,
            Attributes = new() { ["FifoTopic"] = "true", ["ContentBasedDeduplication"] = "false" },
            Tags = TagsKv(),
        })).TopicArn;
        ctx.OnCleanup(Teardown(() => ctx.Sns.DeleteTopicAsync(arn), "NotFound"));
        return arn;
    }

    /// <summary>Create a FIFO queue whose policy admits only this topic, and register its deletion.</summary>
    private static async Task<(string Url, string Arn)> CreateQueueForTopic(FidelityContext ctx, string topicArn)
    {
        var sqs = ctx.Sqs;
        var name = FidelityContext.UniqueName("sim2real-behav-sns-batchnodedup-q-") + ".fifo";
        var url = (await sqs.CreateQueueAsync(new CreateQueueRequest
        {
            QueueName = name,
            Attributes = new() { ["FifoQueue"] = "true" },
            Tags = new() { ["sim2real"] = Scenario, ["sim2real:managed"] = "true" },
        })).QueueUrl;
        ctx.OnCleanup(Teardown(() => sqs.DeleteQueueAsync(url), "AWS.SimpleQueueService.NonExistentQueue"));
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

    /// <summary>Subscribe the queue with raw delivery and poll until the subscription is confirmed.</summary>
    private static async Task<string> SubscribeAndConfirm(FidelityContext ctx, string topicArn, string queueArn, double timeoutS = 60.0)
    {
        var sns = ctx.Sns;
        var subArn = (await sns.SubscribeAsync(new SubscribeRequest
        {
            TopicArn = topicArn, Protocol = "sqs", Endpoint = queueArn,
            Attributes = new() { ["RawMessageDelivery"] = "true" }, ReturnSubscriptionArn = true,
        })).SubscriptionArn;
        ctx.OnCleanup(Teardown(() => sns.UnsubscribeAsync(subArn), "NotFound"));
        var deadline = FidelityTime.StartNew();
        string? pending = null;
        while (deadline.Elapsed < TimeSpan.FromSeconds(timeoutS))
        {
            pending = (await sns.GetSubscriptionAttributesAsync(subArn)).Attributes?.GetValueOrDefault("PendingConfirmation");
            if (pending == "false")
            {
                return subArn;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(1.0));
        }

        throw Fail($"subscription '{subArn}' still reports PendingConfirmation='{pending}' after {timeoutS}s");
    }

    /// <summary>Receive and delete messages up to and including <paramref name="marker"/>; return everything seen first.</summary>
    private static async Task<List<string>> DrainUntilBody(FidelityContext ctx, string queueUrl, string marker, double timeoutS = 120.0)
    {
        var sqs = ctx.Sqs;
        var ahead = new List<string>();
        var deadline = FidelityTime.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(timeoutS))
        {
            var page = await sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = queueUrl, MaxNumberOfMessages = 10, WaitTimeSeconds = 5 });
            foreach (var msg in page.Messages ?? [])
            {
                await sqs.DeleteMessageAsync(queueUrl, msg.ReceiptHandle);
                var body = msg.Body ?? "";
                if (body == marker)
                {
                    return ahead;
                }

                ahead.Add(body);
            }
        }

        throw Fail($"the FIFO sentinel '{marker}' never arrived within {timeoutS}s");
    }

    /// <summary>
    /// Call fn(); return (code, message) of the ClientError it raises. An unexpected success
    /// raises, so a target that accepts what AWS refuses never reads as a code mismatch.
    /// </summary>
    private static async Task<(string? Code, string? Message)> Error<T>(Func<Task<T>> fn)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        return error is null
            ? throw Fail($"expected the call to be refused, but it succeeded and returned {result}")
            : (error.Code, error.Message);
    }
}
