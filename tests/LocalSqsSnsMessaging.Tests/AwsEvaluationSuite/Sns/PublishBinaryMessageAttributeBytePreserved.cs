// Translated from behavioral/sns/check_publish_binary_message_attribute_byte_preserved.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Text.Json.Nodes;
using Amazon.SimpleNotificationService.Model;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sns;

/// <summary>sns.publish-binary-message-attribute-byte-preserved</summary>
[Category("AwsEvaluationSuite")]
public sealed class PublishBinaryMessageAttributeBytePreserved
{
    private const string Scenario = "sns-binary-attr-raw-delivery";

    private static List<Amazon.SimpleNotificationService.Model.Tag> TagsKv =>
        [new() { Key = "sim2real", Value = Scenario }, new() { Key = "sim2real:managed", Value = "true" }];

    /// <summary>
    /// A Binary message attribute holding every byte 0x00-0xff reaches a raw-delivery SQS
    /// subscriber as a native Binary SQS attribute, byte-for-byte unchanged.
    /// </summary>
    [Test]
    [DisplayName("sns.publish-binary-message-attribute-byte-preserved")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sns = ctx.Sns;
        var sqs = ctx.Sqs;

        var topicArn = (await sns.CreateTopicAsync(new CreateTopicRequest
        {
            Name = FidelityContext.UniqueName("sim2real-binattr-topic-"),
            Tags = TagsKv,
        })).TopicArn;
        ctx.OnCleanup(() => sns.DeleteTopicAsync(new DeleteTopicRequest { TopicArn = topicArn }));

        var queueUrl = (await sqs.CreateQueueAsync(new CreateQueueRequest
        {
            QueueName = FidelityContext.UniqueName("sim2real-binattr-q-"),
            Tags = new() { ["sim2real"] = Scenario, ["sim2real:managed"] = "true" },
        })).QueueUrl;
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(queueUrl));
        var queueArn = (await sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest { QueueUrl = queueUrl, AttributeNames = ["QueueArn"] })).Attributes["QueueArn"];
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

        var subArn = (await sns.SubscribeAsync(new SubscribeRequest { TopicArn = topicArn, Protocol = "sqs", Endpoint = queueArn })).SubscriptionArn;
        ctx.OnCleanup(() => sns.UnsubscribeAsync(new UnsubscribeRequest { SubscriptionArn = subArn }));
        await sns.SetSubscriptionAttributesAsync(new SetSubscriptionAttributesRequest
        {
            SubscriptionArn = subArn,
            AttributeName = "RawMessageDelivery",
            AttributeValue = "true",
        });

        var payload = Enumerable.Range(0, 256).Select(i => (byte)i).ToArray();
        var bodyToken = FidelityContext.UniqueName("sim2real-binattr-body-");
        using var payloadStream = new MemoryStream(payload);
        await sns.PublishAsync(new PublishRequest
        {
            TopicArn = topicArn,
            Message = bodyToken,
            MessageAttributes = new() { ["payload"] = new() { DataType = "Binary", BinaryValue = payloadStream } },
        });

        var msg = await PollForBody(ctx, queueUrl, bodyToken, timeoutS: 30.0);
        var receivedAttr = msg.MessageAttributes["payload"];
        var receivedBytes = receivedAttr.BinaryValue?.ToArray() ?? [];
        Ensure(receivedBytes.AsSpan().SequenceEqual(payload),
            $"binary attribute corrupted: {Convert.ToHexString(receivedBytes)} != {Convert.ToHexString(payload)}");
        Ensure(receivedAttr.DataType == "Binary", $"binary attribute lost its type over raw delivery: '{receivedAttr.DataType}'");
    }

    private static async Task<Message> PollForBody(FidelityContext ctx, string queueUrl, string bodyToken, double timeoutS = 30.0)
    {
        var sqs = ctx.Sqs;
        var deadline = FidelityTime.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(timeoutS))
        {
            var resp = await sqs.ReceiveMessageAsync(new ReceiveMessageRequest
            {
                QueueUrl = queueUrl,
                MaxNumberOfMessages = 10,
                MessageAttributeNames = ["All"],
                WaitTimeSeconds = 5,
            });
            foreach (var msg in resp.Messages ?? [])
            {
                await sqs.DeleteMessageAsync(new DeleteMessageRequest { QueueUrl = queueUrl, ReceiptHandle = msg.ReceiptHandle });
                if (msg.Body == bodyToken)
                {
                    return msg;
                }
            }
        }

        throw Fail($"message '{bodyToken}' never arrived");
    }
}
