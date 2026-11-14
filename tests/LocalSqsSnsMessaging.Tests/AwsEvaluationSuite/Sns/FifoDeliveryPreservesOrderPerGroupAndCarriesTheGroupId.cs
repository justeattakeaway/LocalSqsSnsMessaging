// Translated from behavioral/sns/check_fifo_delivery_preserves_order_per_group_and_carries_the_group_id.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Text.Json.Nodes;
using Amazon.SimpleNotificationService.Model;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sns;

/// <summary>sns.fifo-delivery-preserves-order-per-group-and-carries-the-group-id</summary>
[Category("AwsEvaluationSuite")]
public sealed class FifoDeliveryPreservesOrderPerGroupAndCarriesTheGroupId
{
    private const string Scenario = "sns-fifo-interleaved-groups";

    /// <summary>
    /// Messages published alternately into two FIFO message groups arrive at a raw-delivery FIFO
    /// queue in publish order within each group, carrying their MessageGroupId and
    /// MessageDeduplicationId as SQS system attributes.
    /// </summary>
    [Test]
    [DisplayName("sns.fifo-delivery-preserves-order-per-group-and-carries-the-group-id")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sns = ctx.Sns;
        var sqs = ctx.Sqs;
        var topicArn = await CreateTopic(ctx, fifo: true);
        var (queueUrl, queueArn) = await CreateQueueForTopic(ctx, topicArn, fifo: true);
        await SubscribeAndConfirm(ctx, topicArn, queueArn,
            attributes: new() { ["RawMessageDelivery"] = "true" });

        var groups = new Dictionary<string, List<string>> { ["alpha"] = [], ["beta"] = [] };
        var published = new Dictionary<string, (string Group, string Dedup)>();
        for (var i = 1; i < 6; i++)
        {
            foreach (var group in new[] { "alpha", "beta" })
            {
                var body = FidelityContext.UniqueName($"sim2real-behav-fifogrp-{group}-{i}-");
                var dedup = FidelityContext.UniqueName($"dedup-{group}-{i}-");
                await sns.PublishAsync(new PublishRequest
                {
                    TopicArn = topicArn,
                    Message = body,
                    MessageGroupId = group,
                    MessageDeduplicationId = dedup,
                });
                groups[group].Add(body);
                published[body] = (group, dedup);
            }
        }

        var received = new List<(string Body, Dictionary<string, string> Attributes)>();
        var deadline = FidelityTime.StartNew();
        while (received.Count < published.Count)
        {
            var resp = await sqs.ReceiveMessageAsync(new ReceiveMessageRequest
            {
                QueueUrl = queueUrl,
                MaxNumberOfMessages = 10,
                WaitTimeSeconds = 5,
                MessageSystemAttributeNames = ["MessageGroupId", "MessageDeduplicationId"],
            });
            foreach (var msg in resp.Messages ?? [])
            {
                await sqs.DeleteMessageAsync(new DeleteMessageRequest { QueueUrl = queueUrl, ReceiptHandle = msg.ReceiptHandle });
                received.Add((msg.Body, msg.Attributes ?? []));
            }

            if (received.Count < published.Count && deadline.Elapsed >= TimeSpan.FromSeconds(120.0))
            {
                throw Fail($"only {received.Count} of {published.Count} FIFO messages arrived within 120s");
            }
        }

        var bodies = received.Select(r => r.Body).ToList();
        Ensure(bodies.Order(StringComparer.Ordinal).SequenceEqual(published.Keys.Order(StringComparer.Ordinal)),
            "the queue must receive each published message exactly once; received " +
            $"{bodies.Count} bodies, {bodies.Distinct().Count()} distinct, " +
            $"{bodies.ToHashSet().Except(published.Keys).Count()} not published");
        foreach (var (group, order) in groups)
        {
            var delivered = bodies.Where(b => published[b].Group == group).ToList();
            Ensure(delivered.SequenceEqual(order),
                $"group '{group}' must arrive in publish order {Repr(order)}, got {Repr(delivered)}");
        }

        foreach (var r in received)
        {
            var (group, dedup) = published[r.Body];
            Ensure(r.Attributes.GetValueOrDefault("MessageGroupId") == group,
                $"'{r.Body}' was published in group '{group}' but arrived with " +
                $"MessageGroupId '{r.Attributes.GetValueOrDefault("MessageGroupId")}'");
            Ensure(r.Attributes.GetValueOrDefault("MessageDeduplicationId") == dedup,
                $"'{r.Body}' was published with MessageDeduplicationId '{dedup}' but arrived " +
                $"with '{r.Attributes.GetValueOrDefault("MessageDeduplicationId")}'");
        }
    }

    private static string Repr(IEnumerable<string> values) => $"[{string.Join(", ", values.Select(v => $"'{v}'"))}]";

    private static List<Amazon.SimpleNotificationService.Model.Tag> TagsKv() =>
        [new() { Key = "sim2real", Value = Scenario }, new() { Key = "sim2real:managed", Value = "true" }];

    /// <summary>CreateTopic (tagged + prefix-named) and register DeleteTopic immediately.</summary>
    private static async Task<string> CreateTopic(FidelityContext ctx, bool fifo = false, Dictionary<string, string>? attributes = null)
    {
        var sns = ctx.Sns;
        var name = FidelityContext.UniqueName("sim2real-behav-sns-t-") + (fifo ? ".fifo" : "");
        var attrs = new Dictionary<string, string>(attributes ?? []);
        if (fifo)
        {
            attrs["FifoTopic"] = "true";
        }

        var arn = (await sns.CreateTopicAsync(new CreateTopicRequest { Name = name, Attributes = attrs, Tags = TagsKv() })).TopicArn;
        ctx.OnCleanup(() => ctx.Sns.DeleteTopicAsync(new DeleteTopicRequest { TopicArn = arn }));
        return arn;
    }

    /// <summary>Create a real SQS queue whose policy lets exactly this topic send to it.</summary>
    private static async Task<(string Url, string Arn)> CreateQueueForTopic(FidelityContext ctx, string topicArn, bool fifo = false,
        Dictionary<string, string>? attributes = null)
    {
        var sqs = ctx.Sqs;
        var name = FidelityContext.UniqueName("sim2real-behav-sns-q-") + (fifo ? ".fifo" : "");
        var attrs = new Dictionary<string, string>(attributes ?? []);
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
