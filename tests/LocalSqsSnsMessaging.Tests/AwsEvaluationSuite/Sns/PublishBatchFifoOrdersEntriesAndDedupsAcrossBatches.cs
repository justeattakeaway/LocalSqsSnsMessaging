// Translated from behavioral/sns/check_publish_batch_fifo_orders_entries_and_dedups_across_batches.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Globalization;
using System.Text.Json.Nodes;
using Amazon.SimpleNotificationService.Model;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sns;

/// <summary>sns.publish-batch-fifo-orders-entries-and-dedups-across-batches</summary>
[Category("AwsEvaluationSuite")]
public sealed class PublishBatchFifoOrdersEntriesAndDedupsAcrossBatches
{
    private const string Scenario = "sns-fifo-batch-order";

    /// <summary>
    /// A ten-entry PublishBatch into one FIFO message group is sequenced and delivered in entry
    /// order, and a later entry reusing an earlier MessageDeduplicationId is Successful but never
    /// delivered.
    /// </summary>
    [Test]
    [DisplayName("sns.publish-batch-fifo-orders-entries-and-dedups-across-batches")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    [KnownFidelityGap(FidelityGap.SnsFifo, "PublishBatch to a FIFO topic returns no SequenceNumber per entry")]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sns = ctx.Sns;
        var sqs = ctx.Sqs;
        var topicArn = await CreateTopic(ctx, fifo: true);
        var (queueUrl, queueArn) = await CreateQueueForTopic(ctx, topicArn, fifo: true);
        await SubscribeAndConfirm(ctx, topicArn, queueArn, attributes: new() { ["RawMessageDelivery"] = "true" });

        const string group = "orders";
        var bodies = Enumerable.Range(1, 10).Select(i => FidelityContext.UniqueName($"sim2real-behav-fifobatch-{i:00}-")).ToList();
        var dedups = Enumerable.Range(1, 10).Select(i => FidelityContext.UniqueName($"dedup-{i:00}-")).ToList();
        var first = await sns.PublishBatchAsync(new PublishBatchRequest
        {
            TopicArn = topicArn,
            PublishBatchRequestEntries = bodies.Select((body, n) => new PublishBatchRequestEntry
            {
                Id = $"e{n + 1:00}", Message = body, MessageGroupId = group, MessageDeduplicationId = dedups[n],
            }).ToList(),
        });
        Ensure((first.Failed ?? []).Count == 0, $"first batch had Failed entries {FailedRepr(first.Failed)}");
        var byId = (first.Successful ?? []).ToDictionary(e => e.Id);
        var wantIds = Enumerable.Range(1, 10).Select(i => $"e{i:00}").ToList();
        Ensure(byId.Keys.Order(StringComparer.Ordinal).SequenceEqual(wantIds),
            $"first batch must report all ten entries Successful, got ids [{string.Join(", ", byId.Keys.Order(StringComparer.Ordinal).Select(k => $"'{k}'"))}]");
        var seqs = wantIds.Select(id => decimal.Parse(
            byId[id].SequenceNumber ?? throw new KeyNotFoundException("'SequenceNumber'"), CultureInfo.InvariantCulture)).ToList();
        Ensure(seqs.Zip(seqs.Skip(1)).All(p => p.First < p.Second),
            $"SequenceNumbers must strictly increase in entry order, got [{string.Join(", ", seqs)}]");

        var duplicate = FidelityContext.UniqueName("sim2real-behav-fifobatch-duplicate-");
        var sentinel = FidelityContext.UniqueName("sim2real-behav-fifobatch-sentinel-");
        var second = await sns.PublishBatchAsync(new PublishBatchRequest
        {
            TopicArn = topicArn,
            PublishBatchRequestEntries =
            [
                new() { Id = "dup", Message = duplicate, MessageGroupId = group, MessageDeduplicationId = dedups[2] },
                new() { Id = "sentinel", Message = sentinel, MessageGroupId = group, MessageDeduplicationId = FidelityContext.UniqueName("dedup-sentinel-") },
            ],
        });
        Ensure((second.Failed ?? []).Count == 0 && (second.Successful ?? []).Select(e => e.Id).Order(StringComparer.Ordinal).SequenceEqual(["dup", "sentinel"]),
            "the second batch must report both entries Successful (deduplication is silent); " +
            $"Successful [{string.Join(", ", (second.Successful ?? []).Select(e => $"'{e.Id}'"))}], " +
            $"Failed {FailedRepr(second.Failed)}");

        // The sentinel follows the duplicate in the same group, so its arrival proves the
        // duplicate is not in flight.
        var received = new List<string>();
        var deadline = FidelityTime.StartNew();
        while (!received.Contains(sentinel))
        {
            var resp = await sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = queueUrl, MaxNumberOfMessages = 10, WaitTimeSeconds = 5 });
            foreach (var msg in resp.Messages ?? [])
            {
                await sqs.DeleteMessageAsync(new DeleteMessageRequest { QueueUrl = queueUrl, ReceiptHandle = msg.ReceiptHandle });
                received.Add(msg.Body);
            }

            if (!received.Contains(sentinel) && deadline.Elapsed >= TimeSpan.FromSeconds(120.0))
            {
                throw Fail($"the sentinel never arrived within 120s; received {received.Count} bodies");
            }
        }

        Ensure(!received.Contains(duplicate),
            "the entry reusing an earlier MessageDeduplicationId inside the deduplication window " +
            "must not be delivered, but it arrived");
        Ensure(received.SequenceEqual([.. bodies, sentinel]),
            "the queue must receive the ten entries in entry order and then the sentinel; got " +
            $"[{string.Join(", ", received.Select(b => $"'{b}'"))}]");
    }

    private static string FailedRepr(List<Amazon.SimpleNotificationService.Model.BatchResultErrorEntry>? failed) => failed is null ? "None" :
        $"[{string.Join(", ", failed.Select(f => $"{{'Id': '{f.Id}', 'Code': '{f.Code}', 'Message': '{f.Message}', 'SenderFault': {f.SenderFault}}}"))}]";

    private static List<Amazon.SimpleNotificationService.Model.Tag> TagsKv() =>
        [new() { Key = "sim2real", Value = Scenario }, new() { Key = "sim2real:managed", Value = "true" }];

    /// <summary>CreateTopic (tagged + prefix-named) and register DeleteTopic immediately.</summary>
    private static async Task<string> CreateTopic(FidelityContext ctx, bool fifo = false)
    {
        var name = FidelityContext.UniqueName("sim2real-behav-sns-t-") + (fifo ? ".fifo" : "");
        var attrs = new Dictionary<string, string>();
        if (fifo)
        {
            attrs["FifoTopic"] = "true";
        }

        var arn = (await ctx.Sns.CreateTopicAsync(new CreateTopicRequest { Name = name, Attributes = attrs, Tags = TagsKv() })).TopicArn;
        ctx.OnCleanup(() => ctx.Sns.DeleteTopicAsync(new DeleteTopicRequest { TopicArn = arn }));
        return arn;
    }

    /// <summary>Create a real SQS queue whose policy lets exactly this topic send to it.</summary>
    private static async Task<(string Url, string Arn)> CreateQueueForTopic(FidelityContext ctx, string topicArn, bool fifo = false)
    {
        var sqs = ctx.Sqs;
        var name = FidelityContext.UniqueName("sim2real-behav-sns-q-") + (fifo ? ".fifo" : "");
        var attrs = new Dictionary<string, string>();
        if (fifo)
        {
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
        await PollUntil(
            async () => (await sns.GetSubscriptionAttributesAsync(new GetSubscriptionAttributesRequest { SubscriptionArn = subArn }))
                .Attributes?.GetValueOrDefault("PendingConfirmation"),
            pending => pending == "false",
            timeoutS: timeoutS, intervalS: 2.0,
            description: "the new subscription leaves PendingConfirmation");
        return subArn;
    }

    /// <summary>Bounded retry; RAISES on the deadline so a timeout can never read as a pass.</summary>
    private static async Task<T> PollUntil<T>(Func<Task<T>> fn, Func<T, bool> pred, double timeoutS, double intervalS, string description)
    {
        var deadline = FidelityTime.StartNew();
        var value = await fn();
        while (!pred(value))
        {
            if (deadline.Elapsed >= TimeSpan.FromSeconds(timeoutS))
            {
                throw Fail($"{description}: not satisfied within {timeoutS}s, last value '{value}'");
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(intervalS));
            value = await fn();
        }

        return value;
    }
}
