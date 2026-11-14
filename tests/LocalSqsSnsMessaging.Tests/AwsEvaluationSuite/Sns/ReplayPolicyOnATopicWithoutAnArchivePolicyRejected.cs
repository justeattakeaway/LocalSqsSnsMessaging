// Translated from behavioral/sns/check_replay_policy_on_a_topic_without_an_archive_policy_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Text.Json;
using System.Text.Json.Nodes;
using Amazon.SimpleNotificationService.Model;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sns;

/// <summary>sns.replay-policy-on-a-topic-without-an-archive-policy-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class ReplayPolicyOnATopicWithoutAnArchivePolicyRejected
{
    private const string Scenario = "sns-replay-without-archive";

    /// <summary>
    /// Subscribing with a ReplayPolicy to a FIFO topic without an ArchivePolicy is refused with an
    /// exact error and creates no subscription, while the identical ReplayPolicy is accepted on a
    /// FIFO topic that archives.
    /// </summary>
    [Test]
    [DisplayName("sns.replay-policy-on-a-topic-without-an-archive-policy-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    [KnownFidelityGap(FidelityGap.SnsArchive, "A FIFO topic created with an ArchivePolicy reports no BeginningArchiveTime attribute")]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sns = ctx.Sns;
        var archiving = await CreateTopic(ctx, fifo: true,
            attributes: new() { ["ArchivePolicy"] = """{"MessageRetentionPeriod": "1"}""" });
        // A topic with an ArchivePolicy cannot be deleted (InvalidState), so the policy is cleared
        // first; LIFO runs this before the DeleteTopic _create_topic registered.
        ctx.OnCleanup(() => sns.SetTopicAttributesAsync(new SetTopicAttributesRequest
        {
            TopicArn = archiving,
            AttributeName = "ArchivePolicy",
            AttributeValue = "{}",
        }));
        var plain = await CreateTopic(ctx, fifo: true);
        var beginning = (await sns.GetTopicAttributesAsync(new GetTopicAttributesRequest { TopicArn = archiving })).Attributes
            .GetValueOrDefault("BeginningArchiveTime");
        Ensure(!string.IsNullOrEmpty(beginning), "the archiving topic must report BeginningArchiveTime");
        // json.dumps({"PointType": "Timestamp", "StartingPoint": beginning})
        var replayPolicy = $$"""{"PointType": "Timestamp", "StartingPoint": {{JsonSerializer.Serialize(beginning)}}}""";

        var (_, plainQueue) = await CreateQueueForTopic(ctx, plain, fifo: true);
        var (code, message) = await Error(() => sns.SubscribeAsync(new SubscribeRequest
        {
            TopicArn = plain,
            Protocol = "sqs",
            Endpoint = plainQueue,
            ReturnSubscriptionArn = true,
            Attributes = new() { ["ReplayPolicy"] = replayPolicy },
        }));
        Ensure(code == "InvalidParameter",
            $"a ReplayPolicy on a non-archiving topic gave '{code}', expected 'InvalidParameter'; " +
            $"message '{message}'");
        Ensure(message == "Invalid parameter: Attributes Reason: ReplayPolicy: Topic ArchivePolicy attribute is missing",
            $"unexpected refusal message '{message}'");
        var subs = new List<Subscription>();
        await foreach (var page in sns.Paginators.ListSubscriptionsByTopic(new ListSubscriptionsByTopicRequest { TopicArn = plain }).Responses)
        {
            subs.AddRange(page.Subscriptions ?? []);
        }

        Ensure(subs.Count == 0, $"the refused Subscribe must create nothing, got {subs.Count} entries");

        // Positive control: the identical ReplayPolicy is accepted by the archiving topic, so the
        // only difference between the two requests is the topic's ArchivePolicy.
        var (_, archivingQueue) = await CreateQueueForTopic(ctx, archiving, fifo: true);
        var sub = await SubscribeAndConfirm(ctx, archiving, archivingQueue, attributes: new() { ["ReplayPolicy"] = replayPolicy });
        var stored = (await sns.GetSubscriptionAttributesAsync(new GetSubscriptionAttributesRequest { SubscriptionArn = sub })).Attributes ?? [];
        Ensure(stored.GetValueOrDefault("ReplayPolicy") == replayPolicy,
            "the archiving topic must store the same ReplayPolicy, got " +
            $"'{stored.GetValueOrDefault("ReplayPolicy")}'");
    }

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
        var arn = (await sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest { QueueUrl = url, AttributeNames = ["QueueArn"] })).Attributes["QueueArn"];
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

    /// <summary>
    /// (Error.Code, Error.Message) of the ClientError fn raises. An unexpected success
    /// raises too, naming what came back, so it can never read as a code mismatch.
    /// </summary>
    private static async Task<(string Code, string Message)> Error<T>(Func<Task<T>> fn)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is null)
        {
            throw Fail($"expected the call to be refused, but it succeeded: {result}");
        }

        return (error.Code ?? "", error.Message ?? "");
    }
}
