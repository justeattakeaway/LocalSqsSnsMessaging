// Translated from behavioral/sns/check_unsubscribe_stops_delivery.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Globalization;
using System.Text.Json.Nodes;
using Amazon.SimpleNotificationService;
using Amazon.SimpleNotificationService.Model;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sns;

/// <summary>sns.unsubscribe-stops-delivery</summary>
[Category("AwsEvaluationSuite")]
public sealed class UnsubscribeStopsDelivery
{
    private static List<Amazon.SimpleNotificationService.Model.Tag> TagsKv() =>
        [new() { Key = "sim2real", Value = "sns-unsubscribe" }, new() { Key = "sim2real:managed", Value = "true" }];

    /// <summary>
    /// A publish before Unsubscribe reaches the SQS subscriber and one after it does not (an empty receive
    /// and ApproximateNumberOfMessages 0 for at least 3x the baseline latency); GetSubscriptionAttributes on
    /// the removed subscription answers NotFound "Subscription does not exist".
    /// </summary>
    [Test]
    [DisplayName("sns.unsubscribe-stops-delivery")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sns = ctx.Sns;
        var topicArn = await CreateTopic(sns, ctx, FidelityContext.UniqueName("sim2real-behav-sns-unsub-"), tags: TagsKv());
        var (queueUrl, queueArn) = await CreateSqsQueueForSns(ctx, topicArn);

        var subArn = (await sns.SubscribeAsync(new SubscribeRequest
        {
            TopicArn = topicArn,
            Protocol = "sqs",
            Endpoint = queueArn,
            Attributes = new() { ["RawMessageDelivery"] = "true" },
        })).SubscriptionArn;
        Ensure(subArn.StartsWith(topicArn + ":", StringComparison.Ordinal), $"expected an auto-confirmed SubscriptionArn, got '{subArn}'");

        var baselinePayload = $"baseline-{Guid.NewGuid()}";
        var baselineStart = FidelityTime.StartNew();
        await sns.PublishAsync(new PublishRequest { TopicArn = topicArn, Message = baselinePayload });
        var baselineBodies = await DrainFor(ctx, queueUrl, durationS: 30, until: baselinePayload);
        var baselineLatencyS = baselineStart.Elapsed.TotalSeconds;
        Ensure(baselineBodies.Contains(baselinePayload),
            "the subscription never delivered a baseline publish, so the absence checked below " +
            $"would prove nothing: got [{string.Join(", ", baselineBodies.Select(b => $"'{b}'"))}]");

        await sns.UnsubscribeAsync(new UnsubscribeRequest { SubscriptionArn = subArn });

        var payload = $"should-not-arrive-{Guid.NewGuid()}";
        await sns.PublishAsync(new PublishRequest { TopicArn = topicArn, Message = payload });

        var holdS = Math.Max(20.0, baselineLatencyS * 3);
        var bodies = await DrainFor(ctx, queueUrl, durationS: holdS);
        Ensure(!bodies.Contains(payload), "message was delivered to a queue after its subscription was removed");
        var depth = await QueueDepth(ctx, queueUrl);
        Ensure(depth == 0, $"queue reports {depth} message(s) still pending after the withheld publish");

        var (result, error) = await FidelityContext.Capture(() =>
            sns.GetSubscriptionAttributesAsync(new GetSubscriptionAttributesRequest { SubscriptionArn = subArn }));
        if (error is null)
        {
            throw Fail($"GetSubscriptionAttributes on the unsubscribed arn SUCCEEDED: {result}");
        }

        Ensure(error.Code == "NotFound", $"expected NotFound for unsubscribed arn, got {error.Code}");
        Ensure(error.Message == "Subscription does not exist", $"unexpected NotFound message for unsubscribed arn: '{error.Message}'");
    }

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
            QueueName = name,
            Attributes = attributes,
            Tags = TagsKv().ToDictionary(t => t.Key, t => t.Value),
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

    /// <summary>
    /// Long-poll a queue across duration_s wall-clock seconds, or until <c>until</c> is among the
    /// bodies; deletes and returns every body seen.
    /// </summary>
    private static async Task<List<string>> DrainFor(FidelityContext ctx, string queueUrl, double durationS, string? until = null)
    {
        var sqs = ctx.Sqs;
        var bodies = new List<string>();
        var clock = FidelityTime.StartNew();
        while (clock.Elapsed < TimeSpan.FromSeconds(durationS) && (until is null || !bodies.Contains(until)))
        {
            var wait = (int)Math.Min(20, Math.Max(1, durationS - clock.Elapsed.TotalSeconds));
            var resp = await sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = queueUrl, MaxNumberOfMessages = 10, WaitTimeSeconds = wait });
            foreach (var m in resp.Messages ?? [])
            {
                await sqs.DeleteMessageAsync(new DeleteMessageRequest { QueueUrl = queueUrl, ReceiptHandle = m.ReceiptHandle });
                bodies.Add(m.Body);
            }
        }

        return bodies;
    }

    /// <summary>ApproximateNumberOfMessages, as an independent observable alongside an empty receive.</summary>
    private static async Task<int> QueueDepth(FidelityContext ctx, string queueUrl)
    {
        var attrs = (await ctx.Sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest
        {
            QueueUrl = queueUrl, AttributeNames = ["ApproximateNumberOfMessages"],
        })).Attributes!;
        return int.Parse(attrs["ApproximateNumberOfMessages"], CultureInfo.InvariantCulture);
    }
}
