// Translated from behavioral/sns/check_publish_batch_aggregate_size_at_the_exact_262144_byte_boundary.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Text.Json.Nodes;
using Amazon.SimpleNotificationService.Model;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sns;

/// <summary>sns.publish-batch-aggregate-size-at-the-exact-262144-byte-boundary</summary>
[Category("AwsEvaluationSuite")]
public sealed class PublishBatchAggregateSizeAtTheExact262144ByteBoundary
{
    private const string Scenario = "sns-publish-batch-aggregate-boundary";
    private const int LimitBytes = 262144;

    /// <summary>
    /// PublishBatch bodies totalling exactly 262144 bytes are accepted and delivered intact, while
    /// one byte more (262145) refuses the whole batch with BatchRequestTooLong.
    /// </summary>
    [Test]
    [DisplayName("sns.publish-batch-aggregate-size-at-the-exact-262144-byte-boundary")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sns = ctx.Sns;
        var topicArn = await CreateTopic(ctx);
        var (queueUrl, queueArn) = await CreateQueueForTopic(ctx, topicArn);
        await SubscribeAndConfirm(ctx, topicArn, queueArn, attributes: new() { ["RawMessageDelivery"] = "true" });

        var runId = FidelityContext.UniqueName("sim2real-behav-sns-aggbound-");
        var headA = $"{runId}-a-";
        var headB = $"{runId}-b-";
        const int half = LimitBytes / 2;
        var bodyAOk = headA + new string('a', half - headA.Length);
        var bodyBOk = headB + new string('b', LimitBytes - half - headB.Length);
        Ensure(bodyAOk.Length + bodyBOk.Length == LimitBytes,
            $"boundary bodies total {bodyAOk.Length + bodyBOk.Length} bytes, expected {LimitBytes}");

        var resp = await sns.PublishBatchAsync(new PublishBatchRequest
        {
            TopicArn = topicArn,
            PublishBatchRequestEntries = [new() { Id = "a", Message = bodyAOk }, new() { Id = "b", Message = bodyBOk }],
        });
        var failed = resp.Failed ?? [];
        Ensure(failed.Count == 0,
            $"a batch whose bodies total exactly {LimitBytes} bytes must be accepted, got Failed " +
            $"[{string.Join(", ", failed.Select(f => $"{{'Id': '{f.Id}', 'Code': '{f.Code}', 'Message': '{f.Message}'}}"))}]");
        var successful = resp.Successful ?? [];
        Ensure(successful.Select(e => e.Id).Order(StringComparer.Ordinal).SequenceEqual(["a", "b"]),
            $"expected both entries Successful, got [{string.Join(", ", successful.Select(e => $"{{'Id': '{e.Id}', 'MessageId': '{e.MessageId}'}}"))}]");

        var delivered = await AwaitBodies(ctx, queueUrl, [bodyAOk, bodyBOk], timeoutS: 90.0,
            description: "the exact-boundary batch is delivered intact");
        Ensure(delivered.ToHashSet().SetEquals([bodyAOk, bodyBOk]),
            $"the boundary batch's bodies must arrive byte-for-byte unmodified, got [{string.Join(", ", delivered.Order(StringComparer.Ordinal).Select(b => $"'{b}'"))}]");

        var bodyBOver = bodyBOk + "z";
        var (code, message) = await Error(() => sns.PublishBatchAsync(new PublishBatchRequest
        {
            TopicArn = topicArn,
            PublishBatchRequestEntries = [new() { Id = "a2", Message = bodyAOk }, new() { Id = "b2", Message = bodyBOver }],
        }));
        Ensure(code == "BatchRequestTooLong",
            $"a batch whose bodies total {LimitBytes + 1} bytes (one over the boundary) gave " +
            $"'{code}', expected 'BatchRequestTooLong'; message '{message}'");
        Ensure(message == "The length of all the messages put together is more than the limit.",
            $"unexpected refusal message '{message}'");
    }

    private static List<Amazon.SimpleNotificationService.Model.Tag> TagsKv() =>
        [new() { Key = "sim2real", Value = Scenario }, new() { Key = "sim2real:managed", Value = "true" }];

    /// <summary>CreateTopic (tagged + prefix-named) and register DeleteTopic immediately; returns the ARN.</summary>
    private static async Task<string> CreateTopic(FidelityContext ctx)
    {
        var name = FidelityContext.UniqueName("sim2real-behav-sns-aggbound-");
        var arn = (await ctx.Sns.CreateTopicAsync(new CreateTopicRequest { Name = name, Tags = TagsKv() })).TopicArn;
        ctx.OnCleanup(() => ctx.Sns.DeleteTopicAsync(new DeleteTopicRequest { TopicArn = arn }));
        return arn;
    }

    /// <summary>Create a real SQS queue whose policy lets exactly this topic publish to it.</summary>
    private static async Task<(string Url, string Arn)> CreateQueueForTopic(FidelityContext ctx, string topicArn)
    {
        var sqs = ctx.Sqs;
        var name = FidelityContext.UniqueName("sim2real-behav-sns-aggbound-q-");
        var url = (await sqs.CreateQueueAsync(new CreateQueueRequest
        {
            QueueName = name, Tags = new() { ["sim2real"] = Scenario, ["sim2real:managed"] = "true" },
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

    /// <summary>Long-poll until every wanted body is seen; RAISES on the deadline. Returns what was seen.</summary>
    private static async Task<List<string>> AwaitBodies(FidelityContext ctx, string queueUrl, HashSet<string> wanted, double timeoutS, string description)
    {
        var sqs = ctx.Sqs;
        var seen = new List<string>();
        var outstanding = new HashSet<string>(wanted, StringComparer.Ordinal);
        var deadline = FidelityTime.StartNew();
        while (outstanding.Count > 0)
        {
            var resp = await sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = queueUrl, MaxNumberOfMessages = 10, WaitTimeSeconds = 5 });
            foreach (var msg in resp.Messages ?? [])
            {
                await sqs.DeleteMessageAsync(new DeleteMessageRequest { QueueUrl = queueUrl, ReceiptHandle = msg.ReceiptHandle });
                var body = msg.Body;
                seen.Add(body);
                outstanding.Remove(body);
            }

            if (outstanding.Count > 0 && deadline.Elapsed >= TimeSpan.FromSeconds(timeoutS))
            {
                throw Fail($"{description}: {outstanding.Count} body(ies) never arrived within {timeoutS}s");
            }
        }

        return seen;
    }

    /// <summary>
    /// (Error.Code, Error.Message) of the ClientError <c>fn</c> raises. An unexpected success
    /// raises too, naming what came back, so it can never read as a code mismatch.
    /// </summary>
    private static async Task<(string? Code, string? Message)> Error<T>(Func<Task<T>> fn)
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
