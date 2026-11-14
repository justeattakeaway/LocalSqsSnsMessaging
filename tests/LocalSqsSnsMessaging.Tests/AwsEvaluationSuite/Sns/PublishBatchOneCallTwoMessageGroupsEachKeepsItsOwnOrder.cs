// Translated from behavioral/sns/check_publish_batch_one_call_two_message_groups_each_keeps_its_own_order.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Globalization;
using System.Numerics;
using System.Text.Json.Nodes;
using Amazon.SimpleNotificationService.Model;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sns;

/// <summary>sns.publish-batch-one-call-two-message-groups-each-keeps-its-own-order</summary>
[Category("AwsEvaluationSuite")]
public sealed class PublishBatchOneCallTwoMessageGroupsEachKeepsItsOwnOrder
{
    private const string Scenario = "sns-batch-two-groups";

    /// <summary>
    /// One PublishBatch interleaving entries for two MessageGroupIds on a FIFO topic delivers each
    /// group in its own entry order, read from each message's MessageGroupId system attribute.
    /// </summary>
    [Test]
    [DisplayName("sns.publish-batch-one-call-two-message-groups-each-keeps-its-own-order")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    [KnownFidelityGap(FidelityGap.SnsFifo, "PublishBatch to a FIFO topic returns Successful entries without a SequenceNumber")]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sns = ctx.Sns;
        var topicArn = await CreateTopic(ctx);
        var (queueUrl, queueArn) = await CreateQueueForTopic(ctx, topicArn);
        await SubscribeAndConfirm(ctx, topicArn, queueArn, attributes: new() { ["RawMessageDelivery"] = "true" });

        var runId = FidelityContext.UniqueName("sim2real-behav-sns-2grp-");
        var groupA = $"{runId}-group-a";
        var groupB = $"{runId}-group-b";
        var bodiesA = Enumerable.Range(0, 3).Select(i => $"{runId}-a{i}").ToList();
        var bodiesB = Enumerable.Range(0, 3).Select(i => $"{runId}-b{i}").ToList();

        var entries = new List<PublishBatchRequestEntry>();
        for (var i = 0; i < 3; i++)
        {
            entries.Add(new PublishBatchRequestEntry
            {
                Id = $"a{i}", Message = bodiesA[i], MessageGroupId = groupA,
                MessageDeduplicationId = $"{runId}-dedup-a{i}",
            });
            entries.Add(new PublishBatchRequestEntry
            {
                Id = $"b{i}", Message = bodiesB[i], MessageGroupId = groupB,
                MessageDeduplicationId = $"{runId}-dedup-b{i}",
            });
        }

        var resp = await sns.PublishBatchAsync(new PublishBatchRequest { TopicArn = topicArn, PublishBatchRequestEntries = entries });
        var failed = resp.Failed ?? [];
        Ensure(failed.Count == 0,
            $"the batch reported failures: [{string.Join(", ", failed.Select(f => $"{{'Id': '{f.Id}', 'Code': '{f.Code}', 'Message': '{f.Message}'}}"))}]");
        var byId = (resp.Successful ?? []).ToDictionary(e => e.Id);
        var expectedIds = entries.Select(e => e.Id).Order(StringComparer.Ordinal).ToList();
        var gotIds = byId.Keys.Order(StringComparer.Ordinal).ToList();
        Ensure(gotIds.SequenceEqual(expectedIds), $"expected all six entries Successful, got {List(gotIds)}");
        var seqsA = Enumerable.Range(0, 3).Select(i => Seq(byId[$"a{i}"])).ToList();
        var seqsB = Enumerable.Range(0, 3).Select(i => Seq(byId[$"b{i}"])).ToList();
        Ensure(seqsA.Zip(seqsA.Skip(1)).All(p => p.First < p.Second),
            $"group A's SequenceNumbers must strictly increase in entry order, got [{string.Join(", ", seqsA)}]");
        Ensure(seqsB.Zip(seqsB.Skip(1)).All(p => p.First < p.Second),
            $"group B's SequenceNumbers must strictly increase in entry order, got [{string.Join(", ", seqsB)}]");

        var received = await DrainUntilCount(ctx, queueUrl, total: 6, timeoutS: 90.0);
        var byGroup = new Dictionary<string, List<string>> { [groupA] = [], [groupB] = [] };
        foreach (var (body, msgGroupId) in received)
        {
            Ensure(byGroup.ContainsKey(msgGroupId),
                $"message '{body}' carried MessageGroupId '{msgGroupId}', neither of this run's " +
                "two groups");
            byGroup[msgGroupId].Add(body);
        }

        Ensure(byGroup[groupA].SequenceEqual(bodiesA),
            $"group A must be delivered in its own entry order {List(bodiesA)}, got {List(byGroup[groupA])}");
        Ensure(byGroup[groupB].SequenceEqual(bodiesB),
            $"group B must be delivered in its own entry order {List(bodiesB)}, got {List(byGroup[groupB])}");
    }

    /// <summary><c>int(entry["SequenceNumber"])</c>: a missing SequenceNumber raises KeyError, as in Python.</summary>
    private static BigInteger Seq(PublishBatchResultEntry entry) =>
        BigInteger.Parse(entry.SequenceNumber ?? throw new KeyNotFoundException("SequenceNumber"), CultureInfo.InvariantCulture);

    private static string List(IEnumerable<string> items) => "[" + string.Join(", ", items.Select(s => $"'{s}'")) + "]";

    private static List<Amazon.SimpleNotificationService.Model.Tag> TagsKv() =>
        [new() { Key = "sim2real", Value = Scenario }, new() { Key = "sim2real:managed", Value = "true" }];

    /// <summary>CreateTopic (FIFO, tagged + prefix-named) and register DeleteTopic immediately.</summary>
    private static async Task<string> CreateTopic(FidelityContext ctx)
    {
        var sns = ctx.Sns;
        var name = FidelityContext.UniqueName("sim2real-behav-sns-2grp-") + ".fifo";
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
        var name = FidelityContext.UniqueName("sim2real-behav-sns-2grp-q-") + ".fifo";
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
    /// Long-poll until <c>total</c> messages have been seen; returns [(body, MessageGroupId), ...]
    /// in receipt order. RAISES on the deadline.
    /// </summary>
    private static async Task<List<(string Body, string GroupId)>> DrainUntilCount(FidelityContext ctx, string queueUrl, int total, double timeoutS)
    {
        var sqs = ctx.Sqs;
        var output = new List<(string, string)>();
        var deadline = FidelityTime.StartNew();
        while (output.Count < total)
        {
            var resp = await sqs.ReceiveMessageAsync(new ReceiveMessageRequest
            {
                QueueUrl = queueUrl, MaxNumberOfMessages = 10, WaitTimeSeconds = 5,
                MessageSystemAttributeNames = ["MessageGroupId"],
            });
            foreach (var msg in resp.Messages ?? [])
            {
                await sqs.DeleteMessageAsync(new DeleteMessageRequest { QueueUrl = queueUrl, ReceiptHandle = msg.ReceiptHandle });
                output.Add((msg.Body, msg.Attributes["MessageGroupId"]));
            }

            if (output.Count < total && deadline.Elapsed >= TimeSpan.FromSeconds(timeoutS))
            {
                throw Fail($"only {output.Count}/{total} messages arrived within {timeoutS}s: " +
                    $"[{string.Join(", ", output.Select(o => $"('{o.Item1}', '{o.Item2}')"))}]");
            }
        }

        return output;
    }
}
