// Translated from behavioral/sns__sqs/check_fifo_missing_dedup_id_rejected_until_content_based_dedup_enabled.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit abd0811).
using System.Text.Json.Nodes;
using Amazon.Runtime;
using Amazon.SimpleNotificationService.Model;
using Amazon.SQS.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.SnsSqs;

/// <summary>sns__sqs.fifo-missing-dedup-id-rejected-until-content-based-dedup-enabled</summary>
[Category("AwsEvaluationSuite")]
public sealed class FifoMissingDedupIdRejectedUntilContentBasedDedupEnabled
{
    private const string Scenario = "sns-sqs-cbd-flip";

    private static readonly (string Code, string Message) MissingDedupId = (
        "InvalidParameter",
        "Invalid parameter: The topic should either have ContentBasedDeduplication enabled " +
        "or MessageDeduplicationId provided explicitly");

    /// <summary>
    /// On a FIFO topic without ContentBasedDeduplication, Publish and a PublishBatch entry that
    /// omit MessageDeduplicationId are both refused; enabling ContentBasedDeduplication via
    /// SetTopicAttributes lets the identical calls through and both land on the bound queue.
    /// </summary>
    [Test]
    [DisplayName("sns__sqs.fifo-missing-dedup-id-rejected-until-content-based-dedup-enabled")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sns = ctx.Sns;
        var topicArn = await CreateTopic(ctx);
        var (queueUrl, queueArn) = await CreateQueueForTopic(ctx, topicArn);
        await SubscribeAndConfirm(ctx, topicArn, queueArn);

        var runId = FidelityContext.UniqueName("sim2real-behav-sns-cbdflip-");
        var groupId = $"{runId}-group";
        var singleBody = $"{runId}-single";
        var batchBody = $"{runId}-batch";

        await ExpectError(() => sns.PublishAsync(new PublishRequest { TopicArn = topicArn, Message = singleBody, MessageGroupId = groupId }));
        await ExpectError(() => sns.PublishBatchAsync(new PublishBatchRequest
        {
            TopicArn = topicArn,
            PublishBatchRequestEntries = [new PublishBatchRequestEntry { Id = "entry", Message = batchBody, MessageGroupId = groupId }],
        }));

        await sns.SetTopicAttributesAsync(new SetTopicAttributesRequest
        {
            TopicArn = topicArn, AttributeName = "ContentBasedDeduplication", AttributeValue = "true",
        });
        var live = (await sns.GetTopicAttributesAsync(topicArn)).Attributes!["ContentBasedDeduplication"];
        Ensure(live == "true", $"expected ContentBasedDeduplication to read back 'true', got '{live}'");

        await sns.PublishAsync(new PublishRequest { TopicArn = topicArn, Message = singleBody, MessageGroupId = groupId });
        var batchResp = await sns.PublishBatchAsync(new PublishBatchRequest
        {
            TopicArn = topicArn,
            PublishBatchRequestEntries = [new PublishBatchRequestEntry { Id = "entry", Message = batchBody, MessageGroupId = groupId }],
        });
        // The .NET SDK may leave an empty Failed list null where boto3 returns [].
        Ensure(batchResp.Failed is null or { Count: 0 },
            $"expected no Failed entries, got [{string.Join(", ", (batchResp.Failed ?? []).Select(f => $"{f.Id}: {f.Code} {f.Message}"))}]");
        var succeededIds = (batchResp.Successful ?? []).Select(e => e.Id).ToHashSet();
        Ensure(succeededIds.SetEquals(["entry"]), $"expected entry 'entry' to succeed, got {{{string.Join(", ", succeededIds.Select(i => $"'{i}'"))}}}");

        var marker = $"{runId}-marker";
        await sns.PublishAsync(new PublishRequest
        {
            TopicArn = topicArn, Message = marker, MessageGroupId = groupId, MessageDeduplicationId = $"{runId}-marker-dedup",
        });
        var delivered = await DrainUntilBody(ctx, queueUrl, marker);
        Ensure(delivered.SequenceEqual([singleBody, batchBody]),
            $"expected exactly the two accepted publishes, in order, got {Repr(delivered)}");
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
        var name = FidelityContext.UniqueName("sim2real-behav-sns-cbdflip-") + ".fifo";
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
        var name = FidelityContext.UniqueName("sim2real-behav-sns-cbdflip-q-") + ".fifo";
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

        throw Fail($"marker '{marker}' never reached the queue within {timeoutS}s");
    }

    /// <summary>Assert that calling <paramref name="fn"/> raises exactly <see cref="MissingDedupId"/>; an unexpected success raises.</summary>
    private static async Task ExpectError<T>(Func<Task<T>> fn)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is null)
        {
            throw Fail($"expected the call to be refused with {Py.Repr(new object[] { MissingDedupId.Code, MissingDedupId.Message })}, but it succeeded: {result}");
        }

        Ensure((error.Code, error.Message) == MissingDedupId,
            $"expected {Py.Repr(new object[] { MissingDedupId.Code, MissingDedupId.Message })}, got {Py.Repr(new object?[] { error.Code, error.Message })}");
    }
}
