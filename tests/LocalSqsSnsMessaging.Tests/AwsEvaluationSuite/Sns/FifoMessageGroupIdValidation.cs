// Translated from behavioral/sns/check_fifo_message_group_id_validation.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Text.Json.Nodes;
using Amazon.SimpleNotificationService.Model;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sns;

/// <summary>sns.fifo-message-group-id-validation</summary>
[Category("AwsEvaluationSuite")]
public sealed class FifoMessageGroupIdValidation
{
    private const string Scenario = "sns-fifo-group-id-validation";

    /// <summary>
    /// A FIFO Publish without MessageGroupId fails InvalidParameter and delivers nothing, while a
    /// grouped one returns a SequenceNumber; a standard topic accepts and delivers a MessageGroupId
    /// publish without returning a SequenceNumber.
    /// </summary>
    [Test]
    [DisplayName("sns.fifo-message-group-id-validation")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    [KnownFidelityGap(FidelityGap.SnsFifo, "Publish to a FIFO topic returns no SequenceNumber")]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sns = ctx.Sns;
        var runId = FidelityContext.UniqueName("sim2real-behav-sns-fifoval-");
        var group = $"{runId}-group";

        var fifoArn = await CreateTopic(ctx, fifo: true, attributes: new() { ["ContentBasedDeduplication"] = "false" });
        var (fifoQueueUrl, fifoQueueArn) = await CreateQueueForTopic(ctx, fifoArn, fifo: true);
        await SubscribeAndConfirm(ctx, fifoArn, fifoQueueArn);

        var controlMarker = $"{runId}-control";
        var controlResp = await sns.PublishAsync(new PublishRequest
        {
            TopicArn = fifoArn,
            Message = controlMarker,
            MessageGroupId = group,
            MessageDeduplicationId = $"{runId}-dedup-control",
        });
        Ensure(!string.IsNullOrEmpty(controlResp.SequenceNumber),
            $"expected FIFO publish with MessageGroupId to return a SequenceNumber, got {Describe(controlResp)}");

        // Keep MessageDeduplicationId so the refusal can only be about the missing group id.
        var rejectedMarker = $"{runId}-rejected";
        var (code, message) = await Error(() => sns.PublishAsync(new PublishRequest
        {
            TopicArn = fifoArn,
            Message = rejectedMarker,
            MessageDeduplicationId = $"{runId}-dedup-rejected",
        }));
        Ensure(code == "InvalidParameter",
            $"expected InvalidParameter for FIFO publish missing MessageGroupId, got '{code}'");
        Ensure(message == "Invalid parameter: The MessageGroupId parameter is required for FIFO topics",
            $"unexpected InvalidParameter message for FIFO publish missing MessageGroupId: '{message}'");

        // FIFO order within the group: once the sentinel lands, every earlier publish has too.
        var sentinel = $"{runId}-sentinel";
        await sns.PublishAsync(new PublishRequest
        {
            TopicArn = fifoArn,
            Message = sentinel,
            MessageGroupId = group,
            MessageDeduplicationId = $"{runId}-dedup-sentinel",
        });
        var bodies = await DrainUntilBody(ctx, fifoQueueUrl, sentinel, timeoutS: 120.0);
        Ensure(bodies.SequenceEqual([controlMarker]),
            $"expected only the accepted control message ahead of the sentinel, got {Show(bodies)}");
        Ensure(!bodies.Contains(rejectedMarker), $"the refused publish was delivered anyway: {Show(bodies)}");

        var stdArn = await CreateTopic(ctx, fifo: false);
        var (stdQueueUrl, stdQueueArn) = await CreateQueueForTopic(ctx, stdArn, fifo: false);
        await SubscribeAndConfirm(ctx, stdArn, stdQueueArn);

        var stdMarker = $"{runId}-std";
        var stdResp = await sns.PublishAsync(new PublishRequest { TopicArn = stdArn, Message = stdMarker, MessageGroupId = "group-x" });
        Ensure(!string.IsNullOrEmpty(stdResp.MessageId), $"expected standard-topic publish to return a MessageId, got {Describe(stdResp)}");
        Ensure(string.IsNullOrEmpty(stdResp.SequenceNumber),
            $"expected no SequenceNumber from a standard topic, got '{stdResp.SequenceNumber}'");
        var stdBodies = await ReceiveUntilBody(ctx, stdQueueUrl, stdMarker, timeoutS: 60.0);
        Ensure(stdBodies.Contains(stdMarker),
            $"expected the standard-topic publish carrying MessageGroupId to actually be delivered, " +
            $"got {Show(stdBodies)}");
    }

    private static string Describe(PublishResponse r) =>
        "{" + string.Join(", ", new[] { ("MessageId", r.MessageId), ("SequenceNumber", r.SequenceNumber) }
            .Where(kv => kv.Item2 is not null).Select(kv => $"'{kv.Item1}': '{kv.Item2}'")) + "}";

    private static string Show(IEnumerable<string> values) => "[" + string.Join(", ", values.Select(v => $"'{v}'")) + "]";

    /// <summary>The AWS error (Code, Message) raised by fn(); raises if fn succeeds.</summary>
    private static async Task<(string? Code, string? Message)> Error(Func<Task<PublishResponse>> fn)
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is null)
        {
            throw Fail($"the call was expected to be rejected but SUCCEEDED, returning {Describe(result!)}");
        }

        return (error.Code, error.Message);
    }

    private static List<Amazon.SimpleNotificationService.Model.Tag> TagsKv() =>
        [new() { Key = "sim2real", Value = Scenario }, new() { Key = "sim2real:managed", Value = "true" }];

    /// <summary>CreateTopic (tagged + prefix-named) and register DeleteTopic immediately; returns the ARN.</summary>
    private static async Task<string> CreateTopic(FidelityContext ctx, bool fifo = false, Dictionary<string, string>? attributes = null)
    {
        var sns = ctx.Sns;
        var name = FidelityContext.UniqueName("sim2real-behav-sns-fifoval-");
        var attrs = new Dictionary<string, string>(attributes ?? []);
        if (fifo)
        {
            name += ".fifo";
            attrs["FifoTopic"] = "true";
        }

        var arn = (await sns.CreateTopicAsync(new CreateTopicRequest { Name = name, Attributes = attrs, Tags = TagsKv() })).TopicArn;
        ctx.OnCleanup(() => ctx.Sns.DeleteTopicAsync(new DeleteTopicRequest { TopicArn = arn }));
        return arn;
    }

    /// <summary>Create a real SQS queue whose policy lets exactly this topic publish to it.</summary>
    private static async Task<(string Url, string Arn)> CreateQueueForTopic(FidelityContext ctx, string topicArn, bool fifo = false)
    {
        var sqs = ctx.Sqs;
        var name = FidelityContext.UniqueName("sim2real-behav-sns-fifoval-q-");
        var attrs = new Dictionary<string, string>();
        if (fifo)
        {
            name += ".fifo";
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

    /// <summary>Subscribe an SQS queue (raw delivery) and poll GetSubscriptionAttributes until confirmed.</summary>
    private static async Task<string> SubscribeAndConfirm(FidelityContext ctx, string topicArn, string queueArn, double timeoutS = 60.0)
    {
        var sns = ctx.Sns;
        var subArn = (await sns.SubscribeAsync(new SubscribeRequest
        {
            TopicArn = topicArn,
            Protocol = "sqs",
            Endpoint = queueArn,
            ReturnSubscriptionArn = true,
            Attributes = new() { ["RawMessageDelivery"] = "true" },
        })).SubscriptionArn;
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

    /// <summary>Receive until the sentinel body arrives; returns everything seen BEFORE it, in order.</summary>
    private static async Task<List<string>> DrainUntilBody(FidelityContext ctx, string queueUrl, string sentinel, double timeoutS = 120.0)
    {
        var sqs = ctx.Sqs;
        var before = new List<string>();
        var deadline = FidelityTime.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(timeoutS))
        {
            var resp = await sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = queueUrl, MaxNumberOfMessages = 10, WaitTimeSeconds = 5 });
            foreach (var msg in resp.Messages ?? [])
            {
                await sqs.DeleteMessageAsync(new DeleteMessageRequest { QueueUrl = queueUrl, ReceiptHandle = msg.ReceiptHandle });
                var body = msg.Body ?? "";
                if (body == sentinel)
                {
                    return before;
                }

                before.Add(body);
            }
        }

        throw Fail($"the FIFO sentinel '{sentinel}' never arrived within {timeoutS}s, so the absence of the " +
            $"refused publish is not bounded; messages seen were {Show(before)}");
    }

    /// <summary>Long-poll until marker is seen (or raise); returns every body observed along the way.</summary>
    private static async Task<List<string>> ReceiveUntilBody(FidelityContext ctx, string queueUrl, string marker, double timeoutS = 60.0)
    {
        var sqs = ctx.Sqs;
        var bodies = new List<string>();
        var deadline = FidelityTime.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(timeoutS))
        {
            var resp = await sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = queueUrl, MaxNumberOfMessages = 10, WaitTimeSeconds = 5 });
            foreach (var msg in resp.Messages ?? [])
            {
                await sqs.DeleteMessageAsync(new DeleteMessageRequest { QueueUrl = queueUrl, ReceiptHandle = msg.ReceiptHandle });
                bodies.Add(msg.Body ?? "");
            }

            if (bodies.Contains(marker))
            {
                return bodies;
            }
        }

        throw Fail($"expected '{marker}' to be delivered within {timeoutS}s, got {Show(bodies)}");
    }
}
