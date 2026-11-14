// Translated from behavioral/sns/check_publish_batch_entry_group_or_dedup_id_over_128_characters_rejects_the_whole_batch.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Text.Json.Nodes;
using Amazon.SimpleNotificationService.Model;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sns;

/// <summary>sns.publish-batch-entry-group-or-dedup-id-over-128-characters-rejects-the-whole-batch</summary>
[Category("AwsEvaluationSuite")]
public sealed class PublishBatchEntryGroupOrDedupIdOver128CharactersRejectsTheWholeBatch
{
    private const string Scenario = "sns-publish-batch-groupdedup-len";

    /// <summary>
    /// A 129-character MessageGroupId or MessageDeduplicationId on one PublishBatch entry fails the
    /// whole batch with InvalidParameter and its siblings are never delivered; at 128 characters the
    /// batch is accepted and every body arrives.
    /// </summary>
    [Test]
    [DisplayName("sns.publish-batch-entry-group-or-dedup-id-over-128-characters-rejects-the-whole-batch")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    [KnownFidelityGap(FidelityGap.SnsBatch, "PublishBatch accepts an entry with a 129-character MessageDeduplicationId (and its siblings) instead of refusing the whole batch with InvalidParameter")]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sns = ctx.Sns;
        var mono = FidelityTime.StartNew();
        var topicArn = await CreateTopic(ctx);
        var (queueUrl, queueArn) = await CreateQueueForTopic(ctx, topicArn);
        await SubscribeAndConfirm(ctx, topicArn, queueArn, attributes: new() { ["RawMessageDelivery"] = "true" });

        var runId = FidelityContext.UniqueName("sim2real-behav-sns-groupdedup-");
        var group = $"{runId}-group";

        // A distinct, well-formed (<=128-character) MessageDeduplicationId.
        string DedupId(int n)
        {
            var stem = $"{runId}-d{n}-";
            return stem + new string('d', 128 - stem.Length);
        }

        var dedup129 = DedupId(0) + "x";
        var good1 = $"{runId}-good-1";
        var good2 = $"{runId}-good-2";

        var (code, message) = await Error(() => sns.PublishBatchAsync(new PublishBatchRequest
        {
            TopicArn = topicArn, PublishBatchRequestEntries =
            [
                new() { Id = "good-1", Message = good1, MessageGroupId = group, MessageDeduplicationId = DedupId(1) },
                new() { Id = "bad", Message = "too-long-dedup", MessageGroupId = group, MessageDeduplicationId = dedup129 },
                new() { Id = "good-2", Message = good2, MessageGroupId = group, MessageDeduplicationId = DedupId(2) },
            ],
        }));
        Ensure(code == "InvalidParameter",
            $"a PublishBatch with one 129-character MessageDeduplicationId gave '{code}', " +
            $"expected 'InvalidParameter'; message '{message}'");
        var expectedMessage = "Invalid parameter: The MessageDeduplicationId parameter can only include " +
            "alphanumeric and punctuation characters. 1 to 128 in length";
        Ensure(message == expectedMessage, $"expected the exact message '{expectedMessage}', got '{message}'");

        var group129 = ($"{runId}-g-" + new string('g', 128))[..129];
        var good3 = $"{runId}-good-3";
        var good4 = $"{runId}-good-4";
        (code, message) = await Error(() => sns.PublishBatchAsync(new PublishBatchRequest
        {
            TopicArn = topicArn, PublishBatchRequestEntries =
            [
                new() { Id = "good-3", Message = good3, MessageGroupId = group, MessageDeduplicationId = DedupId(7) },
                new() { Id = "bad", Message = "too-long-group", MessageGroupId = group129, MessageDeduplicationId = DedupId(8) },
                new() { Id = "good-4", Message = good4, MessageGroupId = group, MessageDeduplicationId = DedupId(9) },
            ],
        }));
        Ensure(code == "InvalidParameter",
            $"a PublishBatch with one 129-character MessageGroupId gave '{code}', " +
            $"expected 'InvalidParameter'; message '{message}'");
        expectedMessage = "Invalid parameter: The MessageGroupId parameter can only include " +
            "alphanumeric and punctuation characters. 1 to 128 in length";
        Ensure(message == expectedMessage, $"expected the exact message '{expectedMessage}', got '{message}'");

        // The positive control's delivery latency bounds the absence window below.
        var controlBody = $"{runId}-control";
        var started = mono.Elapsed.TotalSeconds;
        var ctrl = await sns.PublishBatchAsync(new PublishBatchRequest
        {
            TopicArn = topicArn, PublishBatchRequestEntries =
            [
                new() { Id = "ctrl", Message = controlBody, MessageGroupId = group, MessageDeduplicationId = DedupId(3) },
            ],
        });
        Ensure((ctrl.Failed ?? []).Count == 0 && (ctrl.Successful ?? []).Count == 1,
            $"the positive control (one legal entry alone) must be accepted, got {BatchRepr(ctrl)}");
        var seen = await AwaitBodies(ctx, mono, queueUrl, [controlBody], timeoutS: 60.0,
            description: "the positive control is delivered");
        var latencyS = seen[controlBody] - started;
        var windowS = Math.Max(3.0 * latencyS, 20.0);

        var leaked = await Drain(ctx, queueUrl, windowS: windowS);
        Ensure(!new HashSet<string> { good1, good2, good3, good4 }.Overlaps(leaked),
            "the batch was rejected as a whole, so its well-formed siblings must not be delivered " +
            $"-- but queue produced {List(leaked)} within a {windowS:F1}s window (>=3x the " +
            $"{latencyS:F1}s control latency)");

        // Repair: trim the offending id to the 128-character boundary; every entry now succeeds
        // and is delivered carrying the group id it was published with.
        var dedup128Boundary = DedupId(4);
        Ensure(dedup128Boundary.Length == 128, $"boundary dedup id is {dedup128Boundary.Length} characters");
        var repaired = await sns.PublishBatchAsync(new PublishBatchRequest
        {
            TopicArn = topicArn, PublishBatchRequestEntries =
            [
                new() { Id = "good-1", Message = good1, MessageGroupId = group, MessageDeduplicationId = DedupId(5) },
                new() { Id = "fixed", Message = "fixed-dedup", MessageGroupId = group, MessageDeduplicationId = dedup128Boundary },
                new() { Id = "good-2", Message = good2, MessageGroupId = group, MessageDeduplicationId = DedupId(6) },
            ],
        });
        Ensure((repaired.Failed ?? []).Count == 0
            && (repaired.Successful ?? []).Select(e => e.Id).Order(StringComparer.Ordinal).SequenceEqual(["fixed", "good-1", "good-2"]),
            $"the repaired batch must report all three entries Successful, got {BatchRepr(repaired)}");
        var delivered = await AwaitBodies(ctx, mono, queueUrl, [good1, good2, "fixed-dedup"], timeoutS: 90.0,
            description: "the repaired batch is delivered");
        Ensure(delivered.Keys.ToHashSet().SetEquals([good1, good2, "fixed-dedup"]),
            $"the repaired batch must deliver every entry, got {List(delivered.Keys.Order(StringComparer.Ordinal))}");
    }

    private static string List(IEnumerable<string> items) => "[" + string.Join(", ", items.Select(s => $"'{s}'")) + "]";

    private static string BatchRepr(PublishBatchResponse r) =>
        $"{{'Successful': [{string.Join(", ", (r.Successful ?? []).Select(e => $"{{'Id': '{e.Id}', 'MessageId': '{e.MessageId}'}}"))}], " +
        $"'Failed': [{string.Join(", ", (r.Failed ?? []).Select(f => $"{{'Id': '{f.Id}', 'Code': '{f.Code}', 'Message': '{f.Message}'}}"))}]}}";

    private static List<Amazon.SimpleNotificationService.Model.Tag> TagsKv() =>
        [new() { Key = "sim2real", Value = Scenario }, new() { Key = "sim2real:managed", Value = "true" }];

    /// <summary>CreateTopic (FIFO, tagged + prefix-named) and register DeleteTopic immediately.</summary>
    private static async Task<string> CreateTopic(FidelityContext ctx)
    {
        var sns = ctx.Sns;
        var name = FidelityContext.UniqueName("sim2real-behav-sns-groupdedup-") + ".fifo";
        var arn = (await sns.CreateTopicAsync(new CreateTopicRequest
        {
            Name = name, Attributes = new() { ["FifoTopic"] = "true" }, Tags = TagsKv(),
        })).TopicArn;
        ctx.OnCleanup(() => ctx.Sns.DeleteTopicAsync(new DeleteTopicRequest { TopicArn = arn }));
        return arn;
    }

    /// <summary>Create a real FIFO SQS queue whose policy lets exactly this topic send to it.</summary>
    private static async Task<(string Url, string Arn)> CreateQueueForTopic(FidelityContext ctx, string topicArn)
    {
        var sqs = ctx.Sqs;
        var name = FidelityContext.UniqueName("sim2real-behav-sns-groupdedup-q-") + ".fifo";
        var url = (await sqs.CreateQueueAsync(new CreateQueueRequest
        {
            QueueName = name, Attributes = new() { ["FifoQueue"] = "true" },
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

    /// <summary>
    /// Long-poll until every wanted body is seen; RAISES on the deadline. Returns {body: time
    /// first seen}.
    /// </summary>
    private static async Task<Dictionary<string, double>> AwaitBodies(FidelityContext ctx, FidelityStopwatch mono, string queueUrl,
        HashSet<string> wanted, double timeoutS, string description)
    {
        var sqs = ctx.Sqs;
        var seen = new Dictionary<string, double>();
        var outstanding = new HashSet<string>(wanted);
        var deadline = FidelityTime.StartNew();
        while (outstanding.Count > 0)
        {
            var resp = await sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = queueUrl, MaxNumberOfMessages = 10, WaitTimeSeconds = 5 });
            foreach (var msg in resp.Messages ?? [])
            {
                await sqs.DeleteMessageAsync(new DeleteMessageRequest { QueueUrl = queueUrl, ReceiptHandle = msg.ReceiptHandle });
                if (outstanding.Remove(msg.Body))
                {
                    seen[msg.Body] = mono.Elapsed.TotalSeconds;
                }
            }

            if (outstanding.Count > 0 && deadline.Elapsed >= TimeSpan.FromSeconds(timeoutS))
            {
                throw Fail($"{description}: {List(outstanding.Order(StringComparer.Ordinal))} never arrived within {timeoutS}s");
            }
        }

        return seen;
    }

    /// <summary>Long-poll a queue for window_s and return every body it produced.</summary>
    private static async Task<List<string>> Drain(FidelityContext ctx, string queueUrl, double windowS)
    {
        var sqs = ctx.Sqs;
        var output = new List<string>();
        var deadline = FidelityTime.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(windowS))
        {
            var resp = await sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = queueUrl, MaxNumberOfMessages = 10, WaitTimeSeconds = 2 });
            foreach (var msg in resp.Messages ?? [])
            {
                await sqs.DeleteMessageAsync(new DeleteMessageRequest { QueueUrl = queueUrl, ReceiptHandle = msg.ReceiptHandle });
                output.Add(msg.Body);
            }
        }

        return output;
    }

    /// <summary>
    /// (Error.Code, Error.Message) of the ClientError <c>fn</c> raises. An unexpected success
    /// raises too, naming what came back, so it can never read as a code mismatch.
    /// </summary>
    private static async Task<(string Code, string Message)> Error(Func<Task<PublishBatchResponse>> fn)
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is null)
        {
            throw Fail($"expected the call to be refused, but it succeeded: {BatchRepr(result!)}");
        }

        return (error.Code ?? "", error.Message ?? "");
    }
}
