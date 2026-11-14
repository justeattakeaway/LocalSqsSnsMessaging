// Translated from behavioral/sns/check_fifo_concurrent_publishers_across_many_groups_keep_each_group_in_order.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Globalization;
using System.Text.Json.Nodes;
using Amazon.SimpleNotificationService.Model;
using Amazon.SQS;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sns;

/// <summary>sns.fifo-concurrent-publishers-across-many-groups-keep-each-group-in-order</summary>
[Category("AwsEvaluationSuite")]
public sealed class FifoConcurrentPublishersAcrossManyGroupsKeepEachGroupInOrder
{
    private const string Check = "fifo-concurrent-publishers-across-many-groups-keep-each-group-in-order";
    private const int Groups = 8;
    private const int PerGroup = 6;

    /// <summary>
    /// Eight concurrent publishers, one per FIFO message group, each publish six messages; every
    /// message is delivered exactly once under its group, and each group arrives in publish order
    /// with strictly increasing SequenceNumbers.
    /// </summary>
    [Test]
    [DisplayName("sns.fifo-concurrent-publishers-across-many-groups-keep-each-group-in-order")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    [KnownFidelityGap(FidelityGap.SnsFifo, "Publish to a FIFO topic returns no SequenceNumber")]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sns = ctx.Sns;
        var sqs = ctx.Sqs;
        var topic = (await sns.CreateTopicAsync(new CreateTopicRequest
        {
            Name = FidelityContext.UniqueName("sim2real-behav-sns-fifomany-") + ".fifo",
            Attributes = new() { ["FifoTopic"] = "true" },
            Tags = [new() { Key = "sim2real", Value = Check }],
        })).TopicArn;
        ctx.OnCleanup(() => sns.DeleteTopicAsync(new DeleteTopicRequest { TopicArn = topic }));
        var url = (await sqs.CreateQueueAsync(new CreateQueueRequest
        {
            QueueName = FidelityContext.UniqueName("sim2real-behav-sns-fifomany-") + ".fifo",
            Attributes = new() { ["FifoQueue"] = "true" },
            Tags = new() { ["sim2real"] = Check },
        })).QueueUrl;
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(url));
        var qArn = (await sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest { QueueUrl = url, AttributeNames = ["QueueArn"] })).Attributes!["QueueArn"];
        var policy = new JsonObject
        {
            ["Version"] = "2012-10-17",
            ["Statement"] = new JsonArray(new JsonObject
            {
                ["Effect"] = "Allow",
                ["Principal"] = new JsonObject { ["Service"] = "sns.amazonaws.com" },
                ["Action"] = "sqs:SendMessage",
                ["Resource"] = qArn,
                ["Condition"] = new JsonObject { ["ArnEquals"] = new JsonObject { ["aws:SourceArn"] = topic } },
            }),
        };
        await sqs.SetQueueAttributesAsync(new SetQueueAttributesRequest { QueueUrl = url, Attributes = new() { ["Policy"] = policy.ToJsonString() } });
        var sub = (await sns.SubscribeAsync(new SubscribeRequest
        {
            TopicArn = topic, Protocol = "sqs", Endpoint = qArn, ReturnSubscriptionArn = true,
        })).SubscriptionArn;
        ctx.OnCleanup(() => sns.UnsubscribeAsync(new UnsubscribeRequest { SubscriptionArn = sub }));

        var runId = FidelityContext.UniqueName("r");

        async Task<List<(string Body, string Seq)>> PublishGroup(int g)
        {
            var output = new List<(string, string)>();
            for (var i = 0; i < PerGroup; i++)
            {
                var body = $"{runId}-g{g}-m{i}";
                var resp = await sns.PublishAsync(new PublishRequest
                {
                    TopicArn = topic, Message = body, MessageGroupId = $"group-{g}", MessageDeduplicationId = body,
                });
                output.Add((body, resp.SequenceNumber ?? throw new KeyNotFoundException("'SequenceNumber'")));
            }

            return output;
        }

        var results = await Task.WhenAll(Enumerable.Range(0, Groups).Select(g => Task.Run(() => PublishGroup(g))));
        var published = results.Select((sent, g) => (g, sent)).ToDictionary(x => x.g, x => x.sent);

        foreach (var (g, sent) in published)
        {
            var seqs = sent.Select(s => decimal.Parse(s.Seq, CultureInfo.InvariantCulture)).ToList();
            Ensure(seqs.Zip(seqs.Skip(1)).All(p => p.First < p.Second),
                $"group-{g}'s SequenceNumbers must strictly increase in publish order; got [{string.Join(", ", seqs)}]");
        }

        const int total = Groups * PerGroup;
        var delivered = await Drain(sqs, url, total, 180);
        Ensure(delivered.Count == total,
            $"all {total} messages must be delivered exactly once; received {delivered.Count}");
        foreach (var (g, sent) in published)
        {
            var got = delivered.Where(d => d.Group == $"group-{g}")
                .Select(d => ((string?)d.Envelope["Message"], (string?)d.Envelope["SequenceNumber"])).ToList();
            Ensure(got.SequenceEqual(sent.Select(s => ((string?)s.Body, (string?)s.Seq))),
                $"group-{g} must arrive in its publish order with the SequenceNumbers Publish returned; " +
                $"published [{string.Join(", ", sent.Select(s => $"'{Last(s.Body)}'"))}], received " +
                $"[{string.Join(", ", got.Select(s => $"'{Last(s.Item1!)}'"))}]");
        }
    }

    private static string Last(string body) => body[(body.LastIndexOf('-') + 1)..];

    /// <summary>
    /// (MessageGroupId, envelope) in receive order until <c>want</c> arrived or the deadline,
    /// then a short tail to catch duplicates.
    /// </summary>
    private static async Task<List<(string Group, JsonObject Envelope)>> Drain(AmazonSQSClient sqs, string url, int want, double timeoutS)
    {
        var output = new List<(string, JsonObject)>();
        var clock = FidelityTime.StartNew();
        TimeSpan? tail = null;
        while (clock.Elapsed < TimeSpan.FromSeconds(timeoutS) && (tail is null || clock.Elapsed < tail))
        {
            var resp = await sqs.ReceiveMessageAsync(new ReceiveMessageRequest
            {
                QueueUrl = url, MaxNumberOfMessages = 10, WaitTimeSeconds = 2, MessageSystemAttributeNames = ["MessageGroupId"],
            });
            foreach (var m in resp.Messages ?? [])
            {
                output.Add((m.Attributes!["MessageGroupId"], JsonNode.Parse(m.Body)!.AsObject()));
                await sqs.DeleteMessageAsync(new DeleteMessageRequest { QueueUrl = url, ReceiptHandle = m.ReceiptHandle });
            }

            if (output.Count >= want && tail is null)
            {
                tail = clock.Elapsed + TimeSpan.FromSeconds(6);
            }
        }

        return output;
    }
}
