// Translated from behavioral/sns/check_publish_batch_one_invalid_fifo_entry_rejected_and_no_sibling_delivered.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Text.Json.Nodes;
using Amazon.SimpleNotificationService.Model;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sns;

/// <summary>sns.publish-batch-one-invalid-fifo-entry-rejected-and-no-sibling-delivered</summary>
[Category("AwsEvaluationSuite")]
public sealed class PublishBatchOneInvalidFifoEntryRejectedAndNoSiblingDelivered
{
    private const string Scenario = "sns-publish-batch-atomicity";

    /// <summary>
    /// One FIFO entry missing MessageGroupId fails the whole PublishBatch with InvalidParameter and
    /// none of its valid siblings is delivered; repairing that entry publishes and delivers all four.
    /// </summary>
    [Test]
    [DisplayName("sns.publish-batch-one-invalid-fifo-entry-rejected-and-no-sibling-delivered")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    [KnownFidelityGap(FidelityGap.SnsBatch, "A FIFO PublishBatch with one entry missing MessageGroupId publishes the siblings and fails that entry with InternalError instead of refusing the whole request with InvalidParameter")]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sns = ctx.Sns;
        var topicArn = await CreateTopic(ctx, fifo: true, attributes: new() { ["ContentBasedDeduplication"] = "false" });
        var (queueUrl, queueArn) = await CreateQueueForTopic(ctx, topicArn, fifo: true);
        await SubscribeAndConfirm(ctx, topicArn, queueArn, attributes: new() { ["RawMessageDelivery"] = "true" });

        var runId = FidelityContext.UniqueName("sim2real-behav-sns-batch-");
        var group = $"{runId}-group";
        string[] ids = ["e0", "e1", "e2", "e3"];
        var markers = ids.ToDictionary(i => i, i => $"{runId}-{i}");
        const string brokenId = "e2";

        List<PublishBatchRequestEntry> Entries(bool repair, string dedupSuffix)
        {
            var output = new List<PublishBatchRequestEntry>();
            foreach (var i in ids)
            {
                var entry = new PublishBatchRequestEntry { Id = i, Message = markers[i], MessageDeduplicationId = $"{runId}-{dedupSuffix}-{i}" };
                if (repair || i != brokenId)
                {
                    entry.MessageGroupId = group;
                }

                output.Add(entry);
            }

            return output;
        }

        // AWS rejects the whole request, not with a per-entry Failed list.
        var (code, message) = await Error(() => sns.PublishBatchAsync(new PublishBatchRequest
        {
            TopicArn = topicArn, PublishBatchRequestEntries = Entries(repair: false, dedupSuffix: "a"),
        }));
        Ensure(code == "InvalidParameter",
            "a PublishBatch with one FIFO entry missing MessageGroupId was not rejected with " +
            $"InvalidParameter: '{code}'");
        const string expectedMessage = "Invalid parameter: The MessageGroupId parameter is required for FIFO topics";
        Ensure(message == expectedMessage, $"expected the exact message '{expectedMessage}', got '{message}'");

        // FIFO ordering within the group makes the sentinel arrive last, bounding the absence check.
        var sentinel = $"{runId}-sentinel";
        await sns.PublishAsync(new PublishRequest
        {
            TopicArn = topicArn, Message = sentinel, MessageGroupId = group, MessageDeduplicationId = $"{runId}-dedup-sentinel",
        });
        var before = await DrainUntilBody(ctx, queueUrl, sentinel, timeoutS: 120.0);
        var strays = await ReceiveAny(ctx, queueUrl, rounds: 1, waitS: 20);
        var everything = before.Concat(strays).ToList();
        var leaked = everything.Where(b => markers.ContainsValue(b)).ToList();
        Ensure(leaked.Count == 0,
            "the batch was rejected as a whole, so none of its entries may be delivered — but " +
            $"{Repr(leaked)} arrived (everything seen: {Repr(everything)})");

        var repaired = await sns.PublishBatchAsync(new PublishBatchRequest
        {
            TopicArn = topicArn, PublishBatchRequestEntries = Entries(repair: true, dedupSuffix: "b"),
        });
        Ensure((repaired.Failed ?? []).Count == 0, $"the repaired batch reported failures: {FailedRepr(repaired.Failed)}");
        Ensure(repaired.Successful.Select(e => e.Id).Order(StringComparer.Ordinal).SequenceEqual(ids.Order(StringComparer.Ordinal)),
            $"the repaired batch did not report every entry Successful: {SuccessfulRepr(repaired.Successful)}");
        var delivered = await DrainUntilAll(ctx, queueUrl, markers.Values, timeoutS: 120.0);
        Ensure(delivered.Order(StringComparer.Ordinal).SequenceEqual(markers.Values.Order(StringComparer.Ordinal)),
            $"the repaired batch did not deliver every entry: {Repr(delivered)}");
    }

    private static string Repr(IEnumerable<string> items) => "[" + string.Join(", ", items.Select(i => $"'{i}'")) + "]";

    private static string FailedRepr(List<Amazon.SimpleNotificationService.Model.BatchResultErrorEntry>? failed) => failed is null ? "None" :
        $"[{string.Join(", ", failed.Select(f => $"{{'Id': '{f.Id}', 'Code': '{f.Code}', 'Message': '{f.Message}', 'SenderFault': {f.SenderFault}}}"))}]";

    private static string SuccessfulRepr(List<PublishBatchResultEntry>? ok) => ok is null ? "None" :
        $"[{string.Join(", ", ok.Select(e => $"{{'Id': '{e.Id}', 'MessageId': '{e.MessageId}'}}"))}]";

    /// <summary>The AWS error (Code, Message) raised by fn(); raises AssertionError if fn succeeds.</summary>
    private static async Task<(string? Code, string? Message)> Error<T>(Func<Task<T>> fn)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is null)
        {
            throw Fail($"the call was expected to be rejected but SUCCEEDED, returning {(result is PublishBatchResponse r ? $"{{'Successful': {SuccessfulRepr(r.Successful)}, 'Failed': {FailedRepr(r.Failed)}}}" : result)}");
        }

        return (error.Code, error.Message ?? "");
    }

    private static List<Amazon.SimpleNotificationService.Model.Tag> TagsKv() =>
        [new() { Key = "sim2real", Value = Scenario }, new() { Key = "sim2real:managed", Value = "true" }];

    /// <summary>CreateTopic (tagged + prefix-named) and register DeleteTopic immediately; returns the ARN.</summary>
    private static async Task<string> CreateTopic(FidelityContext ctx, bool fifo = false, Dictionary<string, string>? attributes = null)
    {
        var name = FidelityContext.UniqueName("sim2real-behav-sns-t-");
        var attrs = new Dictionary<string, string>(attributes ?? []);
        if (fifo)
        {
            name += ".fifo";
            attrs["FifoTopic"] = "true";
        }

        var arn = (await ctx.Sns.CreateTopicAsync(new CreateTopicRequest { Name = name, Attributes = attrs, Tags = TagsKv() })).TopicArn;
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
                var body = msg.Body ?? "";
                if (body == sentinel)
                {
                    return before;
                }

                before.Add(body);
            }
        }

        throw Fail($"the FIFO sentinel '{sentinel}' never arrived within {timeoutS}s, so the absence of the " +
            $"rejected batch's entries is not bounded; messages seen were {Repr(before)}");
    }

    /// <summary>Long-poll the queue until every wanted marker has been seen; returns the markers collected.</summary>
    private static async Task<List<string>> DrainUntilAll(FidelityContext ctx, string queueUrl, IEnumerable<string> wanted, double timeoutS = 120.0)
    {
        var sqs = ctx.Sqs;
        var seen = new List<string>();
        var outstanding = new HashSet<string>(wanted, StringComparer.Ordinal);
        var deadline = FidelityTime.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(timeoutS) && outstanding.Count > 0)
        {
            var resp = await sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = queueUrl, MaxNumberOfMessages = 10, WaitTimeSeconds = 5 });
            foreach (var msg in resp.Messages ?? [])
            {
                await sqs.DeleteMessageAsync(new DeleteMessageRequest { QueueUrl = queueUrl, ReceiptHandle = msg.ReceiptHandle });
                var body = msg.Body ?? "";
                if (outstanding.Remove(body))
                {
                    seen.Add(body);
                }
            }
        }

        if (outstanding.Count > 0)
        {
            throw Fail($"{Repr(outstanding.Order(StringComparer.Ordinal))} never arrived within {timeoutS}s; received {Repr(seen)}");
        }

        return seen;
    }

    /// <summary>Long-poll a queue <c>rounds</c> times and return every body it produced (expected: none).</summary>
    private static async Task<List<string>> ReceiveAny(FidelityContext ctx, string queueUrl, int rounds, int waitS)
    {
        var sqs = ctx.Sqs;
        var bodies = new List<string>();
        for (var i = 0; i < rounds; i++)
        {
            var resp = await sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = queueUrl, MaxNumberOfMessages = 10, WaitTimeSeconds = waitS });
            foreach (var msg in resp.Messages ?? [])
            {
                await sqs.DeleteMessageAsync(new DeleteMessageRequest { QueueUrl = queueUrl, ReceiptHandle = msg.ReceiptHandle });
                bodies.Add(msg.Body ?? "");
            }
        }

        return bodies;
    }
}
