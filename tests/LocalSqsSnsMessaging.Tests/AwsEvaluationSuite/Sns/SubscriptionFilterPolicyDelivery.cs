// Translated from behavioral/sns/check_subscription_filter_policy_delivery.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Globalization;
using System.Text.Json.Nodes;
using Amazon.SimpleNotificationService;
using Amazon.SimpleNotificationService.Model;
using Amazon.SQS;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;
using SnsMessageAttributeValue = Amazon.SimpleNotificationService.Model.MessageAttributeValue;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sns;

/// <summary>sns.subscription-filter-policy-delivery</summary>
[Category("AwsEvaluationSuite")]
public sealed class SubscriptionFilterPolicyDelivery
{
    private const string Scenario = "sns-subscription-filter-policy-delivery";

    /// <summary>
    /// A subscription FilterPolicy delivers the matching message and withholds the non-matching one
    /// for >=3x the matching delivery latency (floor 15s), after which the queue depth polls to 0.
    /// </summary>
    [Test]
    [DisplayName("sns.subscription-filter-policy-delivery")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sns = ctx.Sns;
        var sqs = ctx.Sqs;
        var topicArn = await CreateTopic(sns, ctx, FidelityContext.UniqueName("sim2real-behav-sns-filter-"), tags: TagsKv());
        var (queueUrl, queueArn) = await CreateSqsQueueForSns(ctx, topicArn);

        var subArn = (await sns.SubscribeAsync(new SubscribeRequest
        {
            TopicArn = topicArn,
            Protocol = "sqs",
            Endpoint = queueArn,
            Attributes = new()
            {
                ["RawMessageDelivery"] = "true",
                ["FilterPolicy"] = new JsonObject { ["category"] = new JsonArray("match-me") }.ToJsonString(),
            },
            ReturnSubscriptionArn = true,
        })).SubscriptionArn;
        ctx.OnCleanup(() => sns.UnsubscribeAsync(new UnsubscribeRequest { SubscriptionArn = subArn }));

        var matching = $"matched-{Guid.NewGuid()}";
        var nonmatching = $"skipped-{Guid.NewGuid()}";

        var r1 = await sns.PublishAsync(new PublishRequest
        {
            TopicArn = topicArn,
            Message = matching,
            MessageAttributes = new() { ["category"] = new SnsMessageAttributeValue { DataType = "String", StringValue = "match-me" } },
        });
        Ensure(!string.IsNullOrEmpty(r1.MessageId), "Publish of the matching message returned no MessageId");

        var (positiveLatency, stray) = await AwaitMessage(ctx, queueUrl, matching, timeoutS: 60.0);
        Ensure(stray.Count == 0, $"unexpected messages arrived before the matching one: {Repr(stray)}");

        var r2 = await sns.PublishAsync(new PublishRequest
        {
            TopicArn = topicArn,
            Message = nonmatching,
            MessageAttributes = new() { ["category"] = new SnsMessageAttributeValue { DataType = "String", StringValue = "other" } },
        });
        Ensure(!string.IsNullOrEmpty(r2.MessageId), "Publish of the non-matching message returned no MessageId");

        var absenceWindow = Math.Max(3 * positiveLatency, 15.0);
        var seen = await DrainFor(ctx, queueUrl, absenceWindow);
        Ensure(!seen.Contains(nonmatching), $"filter policy should have dropped '{nonmatching}', got {Repr(seen)}");

        await PollQueueCounters(ctx, sqs, queueUrl, new() { ["ApproximateNumberOfMessages"] = "0", ["ApproximateNumberOfMessagesNotVisible"] = "0" });
    }

    private static string Repr(IEnumerable<string> items) => "[" + string.Join(", ", items.Select(i => $"'{i}'")) + "]";

    private static string Repr(Dictionary<string, string?> d) =>
        "{" + string.Join(", ", d.Select(kv => $"'{kv.Key}': {(kv.Value is null ? "None" : $"'{kv.Value}'")}")) + "}";

    private static List<Amazon.SimpleNotificationService.Model.Tag> TagsKv() =>
        [new() { Key = "sim2real", Value = Scenario }, new() { Key = "sim2real:managed", Value = "true" }];

    /// <summary>CreateTopic + register best-effort DeleteTopic cleanup; returns the TopicArn.</summary>
    private static async Task<string> CreateTopic(AmazonSimpleNotificationServiceClient client, FidelityContext ctx, string name,
        Dictionary<string, string>? attributes = null, List<Amazon.SimpleNotificationService.Model.Tag>? tags = null)
    {
        var request = new CreateTopicRequest { Name = name };
        if (attributes is { Count: > 0 })
        {
            request.Attributes = attributes;
        }

        if (tags is { Count: > 0 })
        {
            request.Tags = tags;
        }

        var arn = (await client.CreateTopicAsync(request)).TopicArn;
        ctx.OnCleanup(() => client.DeleteTopicAsync(new DeleteTopicRequest { TopicArn = arn }));
        return arn;
    }

    /// <summary>Create a REAL SQS queue whose policy lets the topic publish to it; returns (queue_url, queue_arn).</summary>
    private static async Task<(string Url, string Arn)> CreateSqsQueueForSns(FidelityContext ctx, string topicArn, bool fifo = false)
    {
        var sqs = ctx.Sqs;
        var name = FidelityContext.UniqueName("sim2real-behav-sns-q-");
        var attributes = new Dictionary<string, string>();
        if (fifo)
        {
            name += ".fifo";
            attributes["FifoQueue"] = "true";
        }

        var queueUrl = (await sqs.CreateQueueAsync(new CreateQueueRequest
        {
            QueueName = name, Attributes = attributes,
            Tags = new() { ["sim2real"] = Scenario, ["sim2real:managed"] = "true" },
        })).QueueUrl;
        ctx.OnCleanup(() => ctx.Sqs.DeleteQueueAsync(queueUrl));
        var queueArn = (await sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest { QueueUrl = queueUrl, AttributeNames = ["QueueArn"] })).Attributes!["QueueArn"];
        var policy = new JsonObject
        {
            ["Version"] = "2012-10-17",
            ["Statement"] = new JsonArray(new JsonObject
            {
                ["Effect"] = "Allow",
                ["Principal"] = new JsonObject { ["Service"] = "sns.amazonaws.com" },
                ["Action"] = "sqs:SendMessage",
                ["Resource"] = queueArn,
                ["Condition"] = new JsonObject { ["ArnEquals"] = new JsonObject { ["aws:SourceArn"] = topicArn } },
            }),
        };
        await sqs.SetQueueAttributesAsync(new SetQueueAttributesRequest { QueueUrl = queueUrl, Attributes = new() { ["Policy"] = policy.ToJsonString() } });
        return (queueUrl, queueArn);
    }

    /// <summary>Long-poll until a body equal to needle arrives; returns (elapsed_seconds, other_bodies_seen).</summary>
    private static async Task<(double Elapsed, List<string> Others)> AwaitMessage(FidelityContext ctx, string queueUrl, string needle, double timeoutS = 60.0, double intervalS = 2)
    {
        var sqs = ctx.Sqs;
        var start = FidelityTime.StartNew();
        var others = new List<string>();
        while (start.Elapsed < TimeSpan.FromSeconds(timeoutS))
        {
            var resp = await sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = queueUrl, MaxNumberOfMessages = 10, WaitTimeSeconds = (int)intervalS });
            foreach (var m in resp.Messages ?? [])
            {
                var body = m.Body;
                await sqs.DeleteMessageAsync(new DeleteMessageRequest { QueueUrl = queueUrl, ReceiptHandle = m.ReceiptHandle });
                if (body == needle)
                {
                    return (start.Elapsed.TotalSeconds, others);
                }

                others.Add(body);
            }
        }

        throw Fail($"matching message '{needle}' never arrived within {timeoutS}s; saw {Repr(others)}");
    }

    /// <summary>Long-poll for exactly duration_s, deleting as it drains; returns every body seen.</summary>
    private static async Task<List<string>> DrainFor(FidelityContext ctx, string queueUrl, double durationS, double intervalS = 2)
    {
        var sqs = ctx.Sqs;
        var deadline = FidelityTime.StartNew();
        var bodies = new List<string>();
        while (deadline.Elapsed < TimeSpan.FromSeconds(durationS))
        {
            var resp = await sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = queueUrl, MaxNumberOfMessages = 10, WaitTimeSeconds = (int)intervalS });
            foreach (var m in resp.Messages ?? [])
            {
                bodies.Add(m.Body);
                await sqs.DeleteMessageAsync(new DeleteMessageRequest { QueueUrl = queueUrl, ReceiptHandle = m.ReceiptHandle });
            }
        }

        return bodies;
    }

    /// <summary>A poll's timeout: cap_s, bounded by the runner's remaining time less 5 s.</summary>
    private static double BudgetS(FidelityContext ctx, double capS) => Math.Max(1.0, Math.Min(capS, ctx.TimeLeftSeconds - 5.0));

    /// <summary>
    /// Poll GetQueueAttributes until every named counter equals its expected string value.
    /// The ApproximateNumberOf* counters may lag up to a minute after producers stop
    /// (SQS GetQueueAttributes docs), so a single read is not a verdict.
    /// </summary>
    private static async Task<Dictionary<string, string>> PollQueueCounters(FidelityContext ctx, AmazonSQSClient sqs, string queueUrl,
        Dictionary<string, string?> expected, double capS = 90.0)
    {
        var budgetS = BudgetS(ctx, capS);
        var start = FidelityTime.StartNew();
        var stop = TimeSpan.FromSeconds(Math.Min(capS, budgetS));
        while (true)
        {
            var attrs = (await sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest { QueueUrl = queueUrl, AttributeNames = expected.Keys.ToList() })).Attributes ?? [];
            var observed = expected.Keys.ToDictionary(name => name, name => attrs.GetValueOrDefault(name));
            if (observed.All(kv => kv.Value == expected[kv.Key]))
            {
                return attrs;
            }

            if (start.Elapsed >= stop)
            {
                if (budgetS < capS)
                {
                    throw new TimeoutException(
                        $"the check's time budget ran out after {start.Elapsed.TotalSeconds.ToString("F1", CultureInfo.InvariantCulture)}s, before the " +
                        $"{capS}s counter wait ended (last observed {Repr(observed)}); not graded");
                }

                throw Fail($"queue counters never reached {Repr(expected)}; last observed {Repr(observed)}");
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(3));
        }
    }
}
