// Translated from behavioral/sns/check_publish_size_limit_counts_message_attributes.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.SimpleNotificationService;
using Amazon.SimpleNotificationService.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sns;

/// <summary>sns.publish-size-limit-counts-message-attributes</summary>
[Category("AwsEvaluationSuite")]
public sealed class PublishSizeLimitCountsMessageAttributes
{
    private const int MaxBytes = 262144;   // 256 KB, the SNS published message-size limit

    /// <summary>
    /// The 256 KB Publish limit is counted across the message body AND its attributes: body and
    /// attribute are each accepted alone but refused together, as are many small attributes.
    /// Reference: https://docs.aws.amazon.com/sns/latest/api/API_Publish.html
    /// </summary>
    [Test]
    [DisplayName("sns.publish-size-limit-counts-message-attributes")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sns = ctx.Sns;
        var topicArn = await CreateTopic(sns, ctx, FidelityContext.UniqueName("sim2real-behav-sns-sizeattr-"));

        // A body ~8 KB below the limit, and a ~12 KB attribute value. Either alone is legal;
        // together they exceed 256 KB.
        var body = new string('b', MaxBytes - 8192);
        var attrValue = new string('a', 12288);
        Dictionary<string, MessageAttributeValue> Attrs() => new() { ["bulk"] = new() { DataType = "String", StringValue = attrValue } };

        // Control A: the body alone is under the limit and is accepted.
        var respA = await sns.PublishAsync(new PublishRequest { TopicArn = topicArn, Message = body });
        Ensure(!string.IsNullOrEmpty(respA.MessageId),
            $"expected a {body.Length}-byte body (under the {MaxBytes}-byte limit) to be accepted, got {respA}");

        // Control B: the same attribute with a tiny body is accepted, so the attribute itself is
        // not independently illegal.
        var respB = await sns.PublishAsync(new PublishRequest { TopicArn = topicArn, Message = "tiny", MessageAttributes = Attrs() });
        Ensure(!string.IsNullOrEmpty(respB.MessageId),
            $"expected a {attrValue.Length}-byte attribute with a 4-byte body to be accepted, got {respB}");

        // The assertion: body + attribute together exceed 256 KB and must be refused.
        var total = body.Length + attrValue.Length;
        var (code, message) = await Err(() => sns.PublishAsync(new PublishRequest { TopicArn = topicArn, Message = body, MessageAttributes = Attrs() }));
        Ensure(code == "InvalidParameter",
            $"expected InvalidParameter for a publish totalling ~{total} bytes across body and " +
            $"attributes (limit {MaxBytes}); got {code}");
        Ensure(message == "Invalid parameter: Message too long",
            $"expected the exact message 'Invalid parameter: Message too long', got '{message}'");

        // Same shape via a second, independent attribute-count path: many small attributes
        // summing over the limit, so an emulator that caps per-attribute size rather than total
        // size is also caught.
        var many = Enumerable.Range(0, 40).ToDictionary(i => $"k{i:D2}", _ => new MessageAttributeValue { DataType = "String", StringValue = new string('c', 8192) });
        var (codeMany, messageMany) = await Err(() => sns.PublishAsync(new PublishRequest { TopicArn = topicArn, Message = "small", MessageAttributes = many }));
        Ensure(codeMany == "InvalidParameter",
            "expected InvalidParameter for 40 x 8 KB attributes (~327 KB of attributes) with a " +
            $"5-byte body; got {codeMany}");
        Ensure(messageMany == "Invalid parameter: Message too long",
            $"expected the exact message 'Invalid parameter: Message too long', got '{messageMany}'");

        // Effect absent / control: the topic still accepts a legal publish, so the rejections
        // were about size and not about the topic being poisoned by them.
        var respFinal = await sns.PublishAsync(new PublishRequest
        {
            TopicArn = topicArn,
            Message = "after-rejections",
            MessageAttributes = new() { ["bulk"] = new() { DataType = "String", StringValue = "small" } },
        });
        Ensure(!string.IsNullOrEmpty(respFinal.MessageId),
            $"expected the control publish to still be accepted after the rejections, got {respFinal}");
        Ensure(respFinal.MessageId != respA.MessageId && respFinal.MessageId != respB.MessageId,
            "each accepted publish must mint a distinct MessageId; got a repeat of an earlier id");
    }

    /// <summary>CreateTopic + register best-effort DeleteTopic cleanup; returns the TopicArn.</summary>
    private static async Task<string> CreateTopic(AmazonSimpleNotificationServiceClient client, FidelityContext ctx, string name,
        Dictionary<string, string>? attributes = null)
    {
        var request = new CreateTopicRequest { Name = name };
        if (attributes is { Count: > 0 })
        {
            request.Attributes = attributes;
        }

        request.Tags = [new() { Key = "sim2real", Value = "check_publish_size_limit_counts_message_attributes" }];
        var arn = (await client.CreateTopicAsync(request)).TopicArn;
        ctx.OnCleanup(() => client.DeleteTopicAsync(new DeleteTopicRequest { TopicArn = arn }));
        return arn;
    }

    /// <summary>
    /// Call fn(); return the error (Code, Message). An unexpected success raises.
    /// Only `ClientError` is caught: a client-side `ParamValidationError` never reached the service.
    /// </summary>
    private static async Task<(string? Code, string? Message)> Err<T>(Func<Task<T>> fn)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is null)
        {
            throw Fail($"expected the oversized publish to be rejected, but it SUCCEEDED and returned {result}");
        }

        return (error.Code, error.Message);
    }
}
