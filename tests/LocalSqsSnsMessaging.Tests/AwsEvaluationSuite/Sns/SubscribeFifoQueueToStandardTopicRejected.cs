// Translated from behavioral/sns/check_subscribe_fifo_queue_to_standard_topic_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Text.Json.Nodes;
using Amazon.SimpleNotificationService;
using Amazon.SimpleNotificationService.Model;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sns;

/// <summary>sns.subscribe-fifo-queue-to-standard-topic-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class SubscribeFifoQueueToStandardTopicRejected
{
    private const string Scenario = "sns-fifo-queue-standard-topic";

    /// <summary>
    /// Subscribing a FIFO SQS queue to a standard topic is refused with an exact error and adds no
    /// subscription, while the same queue subscribes to a FIFO topic and a standard queue subscribes
    /// to the standard topic.
    /// </summary>
    [Test]
    [DisplayName("sns.subscribe-fifo-queue-to-standard-topic-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sns = ctx.Sns;
        var standardTopic = await CreateTopic(ctx);
        var fifoTopic = await CreateTopic(ctx, fifo: true);

        var (_, fifoQueueArn) = await CreateQueueForTopic(ctx, standardTopic, fifo: true);

        var (code, message) = await Error(() => sns.SubscribeAsync(new SubscribeRequest
        {
            TopicArn = standardTopic,
            Protocol = "sqs",
            Endpoint = fifoQueueArn,
            ReturnSubscriptionArn = true,
        }));
        Ensure(code == "InvalidParameter",
            $"subscribing a FIFO queue to a standard topic gave '{code}', expected " +
            $"'InvalidParameter'; message '{message}'");
        Ensure(message == "Invalid parameter: Invalid parameter: Endpoint Reason: FIFO SQS Queues can not be subscribed to standard SNS topics",
            $"unexpected refusal message '{message}'");

        var subs = await ListSubs(sns, standardTopic);
        Ensure(subs.Count == 0,
            $"the refused Subscribe must leave the standard topic with no subscription, got " +
            $"{subs.Count} entries with endpoints [{string.Join(", ", subs.Select(s => s.Endpoint == fifoQueueArn ? "True" : "False"))}]");

        // Positive controls: the same FIFO queue IS subscribable to a FIFO topic, and the standard
        // topic does accept an SQS subscription -- only the pairing is refused.
        var fifoSub = await SubscribeAndConfirm(ctx, fifoTopic, fifoQueueArn);
        var attrs = (await sns.GetSubscriptionAttributesAsync(new GetSubscriptionAttributesRequest { SubscriptionArn = fifoSub })).Attributes ?? [];
        Ensure(attrs.GetValueOrDefault("TopicArn") == fifoTopic && attrs.GetValueOrDefault("Endpoint") == fifoQueueArn,
            "the FIFO queue's subscription to the FIFO topic must name that topic and queue; " +
            $"TopicArn matches={(attrs.GetValueOrDefault("TopicArn") == fifoTopic ? "True" : "False")}, " +
            $"Endpoint matches={(attrs.GetValueOrDefault("Endpoint") == fifoQueueArn ? "True" : "False")}");
        var (_, stdQueueArn) = await CreateQueueForTopic(ctx, standardTopic);
        var stdSub = await SubscribeAndConfirm(ctx, standardTopic, stdQueueArn);
        var subArns = (await ListSubs(sns, standardTopic)).Select(s => s.SubscriptionArn).ToList();
        Ensure(subArns.SequenceEqual([stdSub]),
            $"the standard topic must list exactly the standard queue's subscription, got " +
            $"{subArns.Count} entries");
    }

    private static async Task<List<Subscription>> ListSubs(AmazonSimpleNotificationServiceClient sns, string topicArn)
    {
        var subs = new List<Subscription>();
        await foreach (var page in sns.Paginators.ListSubscriptionsByTopic(new ListSubscriptionsByTopicRequest { TopicArn = topicArn }).Responses)
        {
            subs.AddRange(page.Subscriptions ?? []);
        }

        return subs;
    }

    private static List<Amazon.SimpleNotificationService.Model.Tag> TagsKv() =>
        [new() { Key = "sim2real", Value = Scenario }, new() { Key = "sim2real:managed", Value = "true" }];

    /// <summary>CreateTopic (tagged + prefix-named) and register DeleteTopic immediately.</summary>
    private static async Task<string> CreateTopic(FidelityContext ctx, bool fifo = false)
    {
        var sns = ctx.Sns;
        var name = FidelityContext.UniqueName("sim2real-behav-sns-t-") + (fifo ? ".fifo" : "");
        var attrs = new Dictionary<string, string>();
        if (fifo)
        {
            attrs["FifoTopic"] = "true";
        }

        var arn = (await sns.CreateTopicAsync(new CreateTopicRequest { Name = name, Attributes = attrs, Tags = TagsKv() })).TopicArn;
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
    private static async Task<string> SubscribeAndConfirm(FidelityContext ctx, string topicArn, string queueArn, double timeoutS = 60.0)
    {
        var sns = ctx.Sns;
        var subArn = (await sns.SubscribeAsync(new SubscribeRequest { TopicArn = topicArn, Protocol = "sqs", Endpoint = queueArn, ReturnSubscriptionArn = true })).SubscriptionArn;
        ctx.OnCleanup(() => ctx.Sns.UnsubscribeAsync(new UnsubscribeRequest { SubscriptionArn = subArn }));
        var deadline = FidelityTime.StartNew();
        var pending = (await sns.GetSubscriptionAttributesAsync(new GetSubscriptionAttributesRequest { SubscriptionArn = subArn })).Attributes?.GetValueOrDefault("PendingConfirmation");
        while (pending != "false")
        {
            if (deadline.Elapsed >= TimeSpan.FromSeconds(timeoutS))
            {
                throw Fail($"the new subscription leaves PendingConfirmation: not satisfied within {timeoutS}s, last value {(pending is null ? "None" : $"'{pending}'")}");
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(2.0));
            pending = (await sns.GetSubscriptionAttributesAsync(new GetSubscriptionAttributesRequest { SubscriptionArn = subArn })).Attributes?.GetValueOrDefault("PendingConfirmation");
        }

        return subArn;
    }

    /// <summary>
    /// (Error.Code, Error.Message) of the ClientError fn raises. An unexpected success
    /// raises too, naming what came back, so it can never read as a code mismatch.
    /// </summary>
    private static async Task<(string Code, string Message)> Error(Func<Task<SubscribeResponse>> fn)
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is null)
        {
            throw Fail($"expected the call to be refused, but it succeeded: {{'SubscriptionArn': '{result!.SubscriptionArn}'}}");
        }

        return (error.Code ?? "", error.Message ?? "");
    }
}
