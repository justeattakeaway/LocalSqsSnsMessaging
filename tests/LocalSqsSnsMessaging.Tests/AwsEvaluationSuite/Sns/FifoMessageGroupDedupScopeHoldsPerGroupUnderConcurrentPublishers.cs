// Translated from behavioral/sns/check_fifo_message_group_dedup_scope_holds_per_group_under_concurrent_publishers.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Text.Json.Nodes;
using Amazon.SimpleNotificationService.Model;
using Amazon.SQS;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sns;

/// <summary>sns.fifo-message-group-dedup-scope-holds-per-group-under-concurrent-publishers</summary>
[Category("AwsEvaluationSuite")]
public sealed class FifoMessageGroupDedupScopeHoldsPerGroupUnderConcurrentPublishers
{
    private const string Check = "fifo-message-group-dedup-scope-holds-per-group-under-concurrent-publishers";
    private const int Groups = 10;
    private const int Repeats = 3;

    /// <summary>
    /// With FifoThroughputScope=MessageGroup, ten concurrent per-group publishers each send three
    /// bodies under one shared MessageDeduplicationId; exactly one message per group is delivered,
    /// that group's first, so deduplication is scoped per group.
    /// </summary>
    [Test]
    [DisplayName("sns.fifo-message-group-dedup-scope-holds-per-group-under-concurrent-publishers")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sns = ctx.Sns;
        var sqs = ctx.Sqs;
        var topic = (await sns.CreateTopicAsync(new CreateTopicRequest
        {
            Name = FidelityContext.UniqueName("sim2real-behav-sns-fifoscope-") + ".fifo",
            Attributes = new() { ["FifoTopic"] = "true", ["FifoThroughputScope"] = "MessageGroup" },
            Tags = [new() { Key = "sim2real", Value = Check }],
        })).TopicArn;
        ctx.OnCleanup(() => sns.DeleteTopicAsync(new DeleteTopicRequest { TopicArn = topic }));
        var scope = (await sns.GetTopicAttributesAsync(new GetTopicAttributesRequest { TopicArn = topic })).Attributes?.GetValueOrDefault("FifoThroughputScope");
        Ensure(scope == "MessageGroup", $"the topic's FifoThroughputScope reads '{scope}'");
        var url = (await sqs.CreateQueueAsync(new CreateQueueRequest
        {
            QueueName = FidelityContext.UniqueName("sim2real-behav-sns-fifoscope-") + ".fifo",
            Attributes = new()
            {
                ["FifoQueue"] = "true", ["DeduplicationScope"] = "messageGroup",
                ["FifoThroughputLimit"] = "perMessageGroupId",
            },
            Tags = new() { ["sim2real"] = Check },
        })).QueueUrl;
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(url));
        var qArn = (await sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest { QueueUrl = url, AttributeNames = ["QueueArn"] })).Attributes!["QueueArn"];
        await sqs.SetQueueAttributesAsync(new SetQueueAttributesRequest
        {
            QueueUrl = url,
            Attributes = new()
            {
                ["Policy"] = new JsonObject
                {
                    ["Version"] = "2012-10-17",
                    ["Statement"] = new JsonArray(new JsonObject
                    {
                        ["Effect"] = "Allow", ["Principal"] = new JsonObject { ["Service"] = "sns.amazonaws.com" },
                        ["Action"] = "sqs:SendMessage", ["Resource"] = qArn,
                        ["Condition"] = new JsonObject { ["ArnEquals"] = new JsonObject { ["aws:SourceArn"] = topic } },
                    }),
                }.ToJsonString(),
            },
        });
        var sub = (await sns.SubscribeAsync(new SubscribeRequest
        {
            TopicArn = topic, Protocol = "sqs", Endpoint = qArn, ReturnSubscriptionArn = true,
            Attributes = new() { ["RawMessageDelivery"] = "true" },
        })).SubscriptionArn;
        ctx.OnCleanup(() => sns.UnsubscribeAsync(new UnsubscribeRequest { SubscriptionArn = sub }));

        var runId = FidelityContext.UniqueName("r");
        var shared = $"{runId}-shared";

        async Task<string> PublishGroup(int g)
        {
            string? first = null;
            for (var i = 0; i < Repeats; i++)
            {
                var body = $"{runId}-g{g}-try{i}";
                await sns.PublishAsync(new PublishRequest
                {
                    TopicArn = topic, Message = body, MessageGroupId = $"group-{g}",
                    MessageDeduplicationId = shared,
                });
                first ??= body;
            }

            return first!;
        }

        var firstsList = await Task.WhenAll(Enumerable.Range(0, Groups).Select(g => Task.Run(() => PublishGroup(g))));
        var firsts = firstsList.Select((first, g) => (g, first)).ToDictionary(x => x.g, x => x.first);

        var delivered = await Drain(sqs, url, Groups, 120);
        var byGroup = new Dictionary<string, List<string>>();
        foreach (var (grp, body) in delivered)
        {
            if (!byGroup.TryGetValue(grp, out var list))
            {
                byGroup[grp] = list = [];
            }

            list.Add(body);
        }

        var expected = firsts.ToDictionary(kv => $"group-{kv.Key}", kv => new List<string> { kv.Value });
        var equal = byGroup.Count == expected.Count
            && byGroup.All(kv => expected.TryGetValue(kv.Key, out var want) && kv.Value.SequenceEqual(want));
        Ensure(equal,
            "each group must deliver exactly its first publish under the shared deduplication id; " +
            $"got {delivered.Count} message(s) across {byGroup.Count} group(s): " +
            "{" + string.Join(", ", byGroup.OrderBy(kv => kv.Key, StringComparer.Ordinal)
                .Select(kv => $"'{kv.Key}': [{string.Join(", ", kv.Value.Select(b => $"'{b[(b.LastIndexOf('-') + 1)..]}'"))}]")) + "}");
    }

    /// <summary>
    /// (MessageGroupId, body) until <c>want</c> arrived or the deadline, then a tail that would
    /// catch any repeat that escaped deduplication.
    /// </summary>
    private static async Task<List<(string Group, string Body)>> Drain(AmazonSQSClient sqs, string url, int want, double timeoutS)
    {
        var output = new List<(string, string)>();
        var clock = FidelityTime.StartNew();
        var deadline = TimeSpan.FromSeconds(timeoutS);
        TimeSpan? tail = null;
        while (clock.Elapsed < deadline && (tail is null || clock.Elapsed < tail))
        {
            var resp = await sqs.ReceiveMessageAsync(new ReceiveMessageRequest
            {
                QueueUrl = url, MaxNumberOfMessages = 10, WaitTimeSeconds = 2,
                MessageSystemAttributeNames = ["MessageGroupId"],
            });
            foreach (var m in resp.Messages ?? [])
            {
                output.Add((m.Attributes["MessageGroupId"], m.Body));
                await sqs.DeleteMessageAsync(new DeleteMessageRequest { QueueUrl = url, ReceiptHandle = m.ReceiptHandle });
            }

            if (output.Count >= want && tail is null)
            {
                tail = clock.Elapsed + TimeSpan.FromSeconds(8);
            }
        }

        return output;
    }
}
