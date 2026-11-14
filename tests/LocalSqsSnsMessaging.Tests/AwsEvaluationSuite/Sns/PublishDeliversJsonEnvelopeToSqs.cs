// Translated from behavioral/sns/check_publish_delivers_json_envelope_to_sqs.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Text.Json.Nodes;
using Amazon.SimpleNotificationService;
using Amazon.SimpleNotificationService.Model;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sns;

/// <summary>sns.publish-delivers-json-envelope-to-sqs</summary>
[Category("AwsEvaluationSuite")]
public sealed class PublishDeliversJsonEnvelopeToSqs
{
    /// <summary>CreateTopic (tagged) + register best-effort DeleteTopic cleanup; returns the TopicArn.</summary>
    private static async Task<string> CreateTopic(AmazonSimpleNotificationServiceClient client, FidelityContext ctx, string name,
        Dictionary<string, string>? attributes = null, List<Amazon.SimpleNotificationService.Model.Tag>? tags = null)
    {
        var request = new CreateTopicRequest { Name = name };
        if (attributes is { Count: > 0 })
        {
            request.Attributes = attributes;
        }

        request.Tags = tags is { Count: > 0 } ? tags : [new() { Key = "sim2real", Value = "publish-delivers-json-envelope-to-sqs" }];
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
            Tags = new() { ["sim2real"] = "publish-delivers-json-envelope-to-sqs" },
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

    /// <summary>Long-poll an SQS queue until min_count messages are collected (or timeout); deletes as it drains.</summary>
    private static async Task<List<Message>> PollQueueForMessages(FidelityContext ctx, string queueUrl, int minCount = 1, double timeoutS = 90, double intervalS = 2)
    {
        var sqs = ctx.Sqs;
        var collected = new List<Message>();
        var deadline = FidelityTime.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(timeoutS) && collected.Count < minCount)
        {
            var resp = await sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = queueUrl, MaxNumberOfMessages = 10, WaitTimeSeconds = (int)intervalS });
            var msgs = resp.Messages ?? [];
            foreach (var m in msgs)
            {
                collected.Add(m);
                await sqs.DeleteMessageAsync(new DeleteMessageRequest { QueueUrl = queueUrl, ReceiptHandle = m.ReceiptHandle });
            }

            if (msgs.Count == 0)
            {
                await FidelityTime.Delay(TimeSpan.FromSeconds(intervalS));
            }
        }

        return collected;
    }

    /// <summary>
    /// Without RawMessageDelivery, an SQS subscriber receives a JSON envelope with
    /// Type=Notification, the TopicArn, Subject, the Publish MessageId and the verbatim Message.
    /// </summary>
    [Test]
    [DisplayName("sns.publish-delivers-json-envelope-to-sqs")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sns = ctx.Sns;
        var topicArn = await CreateTopic(sns, ctx, FidelityContext.UniqueName("sim2real-behav-sns-envelope-"));
        var (queueUrl, queueArn) = await CreateSqsQueueForSns(ctx, topicArn);

        var subArn = (await sns.SubscribeAsync(new SubscribeRequest { TopicArn = topicArn, Protocol = "sqs", Endpoint = queueArn })).SubscriptionArn;
        if (!string.IsNullOrEmpty(subArn) && subArn != "pending confirmation")
        {
            ctx.OnCleanup(() => sns.UnsubscribeAsync(new UnsubscribeRequest { SubscriptionArn = subArn }));
        }

        var payload = $"payload-{Guid.NewGuid()}";
        var msgId = (await sns.PublishAsync(new PublishRequest { TopicArn = topicArn, Subject = "probe-subject", Message = payload })).MessageId;
        Ensure(!string.IsNullOrEmpty(msgId), "Publish must return a MessageId");

        var messages = await PollQueueForMessages(ctx, queueUrl, minCount: 1, timeoutS: 90, intervalS: 2);
        Ensure(messages.Count > 0, "no message was delivered to the SQS subscriber");
        var body = JsonNode.Parse(messages[0].Body)!.AsObject();
        Ensure((string?)body["Type"] == "Notification", $"unexpected envelope Type: {Repr(body["Type"])}");
        Ensure((string?)body["Message"] == payload, $"payload mismatch: {Repr(body["Message"])}");
        Ensure((string?)body["TopicArn"] == topicArn, body.ToJsonString());
        Ensure((string?)body["MessageId"] == msgId,
            $"envelope MessageId {Repr(body["MessageId"])} != Publish MessageId '{msgId}'");
        Ensure((string?)body["Subject"] == "probe-subject", body.ToJsonString());
    }

    private static string Repr(JsonNode? value) => value is null ? "None" : $"'{(string?)value}'";
}
