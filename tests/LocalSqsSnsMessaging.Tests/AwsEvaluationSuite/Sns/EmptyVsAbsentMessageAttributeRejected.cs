// Translated from behavioral/sns/check_empty_vs_absent_message_attribute_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Text.Json.Nodes;
using Amazon.SimpleNotificationService.Model;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;
using SnsMessageAttributeValue = Amazon.SimpleNotificationService.Model.MessageAttributeValue;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sns;

/// <summary>sns.empty-vs-absent-message-attribute-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class EmptyVsAbsentMessageAttributeRejected
{
    private const string Scenario = "sns-empty-vs-absent-attr";

    /// <summary>
    /// Publish refuses an empty-string message attribute value with ParameterValueInvalid and
    /// delivers nothing, while a sibling Publish omitting the attribute arrives without that key.
    /// </summary>
    [Test]
    [DisplayName("sns.empty-vs-absent-message-attribute-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    [KnownFidelityGap(FidelityGap.SnsValidation, "Publish accepts a message attribute with an empty value instead of refusing it with InvalidParameterValue")]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sns = ctx.Sns;
        var sqs = ctx.Sqs;

        var topicArn = (await sns.CreateTopicAsync(new CreateTopicRequest
        {
            Name = FidelityContext.UniqueName("sim2real-emptyattr-topic-"),
            Tags = [new() { Key = "sim2real", Value = Scenario }],
        })).TopicArn;
        ctx.OnCleanup(() => sns.DeleteTopicAsync(new DeleteTopicRequest { TopicArn = topicArn }));

        var queueUrl = (await sqs.CreateQueueAsync(new CreateQueueRequest
        {
            QueueName = FidelityContext.UniqueName("sim2real-emptyattr-q-"),
            Tags = new() { ["sim2real"] = Scenario },
        })).QueueUrl;
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(queueUrl));
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

        var subArn = (await sns.SubscribeAsync(new SubscribeRequest { TopicArn = topicArn, Protocol = "sqs", Endpoint = queueArn })).SubscriptionArn;
        ctx.OnCleanup(() => sns.UnsubscribeAsync(new UnsubscribeRequest { SubscriptionArn = subArn }));
        await sns.SetSubscriptionAttributesAsync(new SetSubscriptionAttributesRequest { SubscriptionArn = subArn, AttributeName = "RawMessageDelivery", AttributeValue = "true" });

        var blankBody = FidelityContext.UniqueName("sim2real-emptyattr-blank-");
        var (code, message) = await Error(
            () => sns.PublishAsync(new PublishRequest
            {
                TopicArn = topicArn,
                Message = blankBody,
                MessageAttributes = new() { ["marker"] = new SnsMessageAttributeValue { DataType = "String", StringValue = "" } },
            }));
        Ensure(code == "ParameterValueInvalid",
            $"expected ParameterValueInvalid for an empty-string message attribute value, got '{code}'");
        Ensure(message == "The message attribute 'marker' must contain non-empty message attribute value for message attribute type 'String'.",
            $"unexpected ParameterValueInvalid message for an empty-string attribute value: '{message}'");

        // Draining until this later publish arrives bounds the absence check for the rejected one.
        var omittedBody = FidelityContext.UniqueName("sim2real-emptyattr-omitted-");
        await sns.PublishAsync(new PublishRequest { TopicArn = topicArn, Message = omittedBody });

        var (bodies, controlMsg) = await DrainUntilContaining(ctx, queueUrl, omittedBody, timeoutS: 60.0);
        Ensure(!bodies.Contains(blankBody), $"the rejected publish '{blankBody}' was delivered anyway: {Show(bodies)}");
        var controlAttrs = controlMsg.MessageAttributes ?? [];
        Ensure(!controlAttrs.ContainsKey("marker"),
            $"an omitted attribute must not reappear on delivery: {{{string.Join(", ", controlAttrs.Select(kv => $"'{kv.Key}': {{'StringValue': '{kv.Value.StringValue}', 'DataType': '{kv.Value.DataType}'}}"))}}}");
    }

    private static string Show(IEnumerable<string> values) => "[" + string.Join(", ", values.Select(v => $"'{v}'")) + "]";

    /// <summary>
    /// Long-poll until a message whose body equals needle arrives; returns every body seen up
    /// to and including it (so the caller can assert on what else came through) plus that message
    /// itself (so the caller can inspect its attributes).
    /// </summary>
    private static async Task<(List<string> Bodies, Message Msg)> DrainUntilContaining(FidelityContext ctx, string queueUrl, string needle, double timeoutS = 60.0)
    {
        var sqs = ctx.Sqs;
        var bodies = new List<string>();
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
                bodies.Add(msg.Body);
                if (msg.Body == needle)
                {
                    return (bodies, msg);
                }
            }
        }

        throw Fail($"the control publish '{needle}' never arrived within {timeoutS}s, so the absence of " +
            $"its sibling is not bounded; bodies seen: {Show(bodies)}");
    }

    /// <summary>(Code, Message) of the ClientError fn() raises; an unexpected success raises.</summary>
    private static async Task<(string? Code, string? Message)> Error<T>(Func<Task<T>> fn)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is null)
        {
            throw Fail($"the call was expected to be rejected but SUCCEEDED, returning {Describe(result)}");
        }

        return (error.Code, error.Message);
    }

    private static string Describe(object? result) => result is PublishResponse p ? $"{{'MessageId': '{p.MessageId}'}}" : $"{result}";
}
