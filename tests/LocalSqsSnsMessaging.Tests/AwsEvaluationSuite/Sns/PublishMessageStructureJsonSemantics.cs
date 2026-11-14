// Translated from behavioral/sns/check_publish_message_structure_json_semantics.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Text.Json.Nodes;
using Amazon.SimpleNotificationService;
using Amazon.SimpleNotificationService.Model;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sns;

/// <summary>sns.publish-message-structure-json-semantics</summary>
[Category("AwsEvaluationSuite")]
public sealed class PublishMessageStructureJsonSemantics
{
    /// <summary>
    /// MessageStructure=json requires a 'default' key; a subscriber-protocol-specific
    /// key overrides 'default' at delivery, and 'default' is the fallback when no such
    /// key is present -- both verified by an independent read at a real SQS subscriber.
    /// </summary>
    [Test]
    [DisplayName("sns.publish-message-structure-json-semantics")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sns = ctx.Sns;
        var topicArn = await CreateTopic(sns, ctx, FidelityContext.UniqueName("sim2real-behav-sns-jsonstruct-"));
        var queueUrl = await SubscribeRawSqs(ctx, sns, topicArn);

        var controlBody = $"positive-control-{Guid.NewGuid()}";
        var controlResp = await sns.PublishAsync(new PublishRequest
        {
            TopicArn = topicArn,
            Message = $$"""{"default": "{{controlBody}}"}""",
            MessageStructure = "json",
        });
        Ensure(!string.IsNullOrEmpty(controlResp.MessageId),
            $"positive control: expected MessageId for a valid json-structured publish, got {{'MessageId': '{controlResp.MessageId}'}}");
        await PollForBody(ctx, queueUrl, controlBody);

        var (code, message) = await Error(() => sns.PublishAsync(new PublishRequest
        {
            TopicArn = topicArn,
            Message = """{"email": "only-email"}""",
            MessageStructure = "json",
        }));
        Ensure(code == "InvalidParameter", $"expected InvalidParameter for json message missing 'default' key, got {code}");
        Ensure(message == "Invalid parameter: Message Structure - No default entry in JSON message body",
            $"expected the exact message 'Invalid parameter: Message Structure - No default entry in JSON message body', got '{message}'");
        var (code2, message2) = await Error(() => sns.PublishAsync(new PublishRequest
        {
            TopicArn = topicArn,
            Message = "not-json-at-all",
            MessageStructure = "json",
        }));
        Ensure(code2 == "InvalidParameter", $"expected InvalidParameter for unparseable json message body, got {code2}");
        Ensure(message2 == "Invalid parameter: Message Structure - JSON message body failed to parse",
            $"expected the exact message 'Invalid parameter: Message Structure - JSON message body failed to parse', got '{message2}'");

        var overrideBody = $"sqs-override-{Guid.NewGuid()}";
        var defaultBody = $"default-body-{Guid.NewGuid()}";
        var resp = await sns.PublishAsync(new PublishRequest
        {
            TopicArn = topicArn,
            Message = $$"""{"default": "{{defaultBody}}", "sqs": "{{overrideBody}}", "email": "email-body"}""",
            MessageStructure = "json",
        });
        Ensure(!string.IsNullOrEmpty(resp.MessageId), $"expected MessageId for a valid json-structured publish, got {{'MessageId': '{resp.MessageId}'}}");
        await PollForBody(ctx, queueUrl, overrideBody);

        var fallbackBody = $"fallback-default-{Guid.NewGuid()}";
        var resp2 = await sns.PublishAsync(new PublishRequest
        {
            TopicArn = topicArn,
            Message = $$"""{"default": "{{fallbackBody}}", "email": "email-body-only"}""",
            MessageStructure = "json",
        });
        Ensure(!string.IsNullOrEmpty(resp2.MessageId),
            $"expected MessageId for a valid json-structured publish with no sqs override, got {{'MessageId': '{resp2.MessageId}'}}");
        await PollForBody(ctx, queueUrl, fallbackBody);
    }

    /// <summary>Call fn(); return the botocore error (Code, Message). An unexpected SUCCESS raises.</summary>
    private static async Task<(string? Code, string? Message)> Error<T>(Func<Task<T>> fn)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is null)
        {
            throw Fail($"expected the call to be rejected, but it SUCCEEDED and returned {result}");
        }

        return (error.Code, error.Message);
    }

    /// <summary>CreateTopic + register best-effort DeleteTopic cleanup; returns the TopicArn.</summary>
    private static async Task<string> CreateTopic(AmazonSimpleNotificationServiceClient client, FidelityContext ctx, string name)
    {
        var arn = (await client.CreateTopicAsync(new CreateTopicRequest { Name = name })).TopicArn;
        ctx.OnCleanup(() => client.DeleteTopicAsync(new DeleteTopicRequest { TopicArn = arn }));
        return arn;
    }

    /// <summary>
    /// Create a real SQS queue subscribed to topic_arn with RawMessageDelivery, so a
    /// received Body is exactly the per-protocol message text SNS resolved for the
    /// subscriber's protocol -- no envelope to unwrap. Returns the queue_url.
    /// </summary>
    private static async Task<string> SubscribeRawSqs(FidelityContext ctx, AmazonSimpleNotificationServiceClient sns, string topicArn,
        string namePrefix = "jsonstruct-q-")
    {
        var sqs = ctx.Sqs;
        var name = FidelityContext.UniqueName("sim2real-behav-" + namePrefix);
        var queueUrl = (await sqs.CreateQueueAsync(new CreateQueueRequest { QueueName = name })).QueueUrl;
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
        var subArn = (await sns.SubscribeAsync(new SubscribeRequest
        {
            TopicArn = topicArn,
            Protocol = "sqs",
            Endpoint = queueArn,
            Attributes = new() { ["RawMessageDelivery"] = "true" },
        })).SubscriptionArn;
        ctx.OnCleanup(() => sns.UnsubscribeAsync(new UnsubscribeRequest { SubscriptionArn = subArn }));
        return queueUrl;
    }

    /// <summary>
    /// Poll the queue (deleting what it reads) until a message with this exact Body
    /// arrives. Raises with the mismatched bodies seen if the deadline passes first.
    /// </summary>
    private static async Task PollForBody(FidelityContext ctx, string queueUrl, string expectedBody, double timeoutS = 60, int intervalS = 2)
    {
        var sqs = ctx.Sqs;
        var deadline = FidelityTime.StartNew();
        var seen = new List<string>();
        while (deadline.Elapsed < TimeSpan.FromSeconds(timeoutS))
        {
            var resp = await sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = queueUrl, MaxNumberOfMessages = 10, WaitTimeSeconds = intervalS });
            foreach (var m in resp.Messages ?? [])
            {
                await sqs.DeleteMessageAsync(new DeleteMessageRequest { QueueUrl = queueUrl, ReceiptHandle = m.ReceiptHandle });
                if (m.Body == expectedBody)
                {
                    return;
                }

                seen.Add(m.Body);
            }
        }

        throw Fail($"expected a delivered SQS body of '{expectedBody}' within {timeoutS}s, saw [{string.Join(", ", seen.Select(b => $"'{b}'"))}]");
    }
}
