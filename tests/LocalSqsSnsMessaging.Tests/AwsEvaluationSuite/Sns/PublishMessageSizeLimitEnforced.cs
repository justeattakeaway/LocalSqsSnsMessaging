// Translated from behavioral/sns/check_publish_message_size_limit_enforced.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.SimpleNotificationService;
using Amazon.SimpleNotificationService.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sns;

/// <summary>sns.publish-message-size-limit-enforced</summary>
[Category("AwsEvaluationSuite")]
public sealed class PublishMessageSizeLimitEnforced
{
    private const string Check = "publish-message-size-limit-enforced";

    /// <summary>Publish must reject a message larger than the 256 KB (262144 byte) payload limit.</summary>
    [Test]
    [DisplayName("sns.publish-message-size-limit-enforced")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ev = ctx.Sns;
        var topicArn = await CreateTopic(ev, ctx, FidelityContext.UniqueName("sim2real-behav-sns-toobig-"));
        var oversized = new string('x', 262144 + 1024);
        var error = await FidelityContext.CaptureError(() => ev.PublishAsync(new PublishRequest { TopicArn = topicArn, Message = oversized }));
        if (error is null)
        {
            throw Fail("a Publish over the 262144-byte limit was expected to be rejected, but it SUCCEEDED");
        }

        var (code, message) = (error.Code, error.Message);
        Ensure(code == "InvalidParameter", $"expected InvalidParameter for message over the 256KB limit, got {code}");
        Ensure(message == "Invalid parameter: Message too long",
            $"expected the exact message 'Invalid parameter: Message too long', got '{message}'");
        // Effect absent / control: the topic still accepts a legal publish afterwards, so the
        // rejection was about size and not about the topic being poisoned by it. This is also the
        // independent terminal read: a target that returned InvalidParameter for every publish,
        // oversized or not, must fail here.
        var ok = new string('y', 262144 - 1024);
        var resp = await ev.PublishAsync(new PublishRequest { TopicArn = topicArn, Message = ok });
        Ensure(!string.IsNullOrEmpty(resp.MessageId),
            $"expected a {ok.Length}-byte publish (under the 262144-byte limit) to be accepted after " +
            $"the rejection, got {{'MessageId': '{resp.MessageId}'}}");
    }

    /// <summary>CreateTopic (tagged so a leftover can be found) + register DeleteTopic cleanup; returns the TopicArn.</summary>
    private static async Task<string> CreateTopic(AmazonSimpleNotificationServiceClient client, FidelityContext ctx, string name,
        Dictionary<string, string>? attributes = null)
    {
        var arn = (await client.CreateTopicAsync(new CreateTopicRequest
        {
            Name = name,
            Attributes = attributes ?? [],
            Tags = [new() { Key = "sim2real", Value = Check }, new() { Key = "sim2real:managed", Value = "true" }],
        })).TopicArn;
        ctx.OnCleanup(() => client.DeleteTopicAsync(new DeleteTopicRequest { TopicArn = arn }));
        return arn;
    }
}
