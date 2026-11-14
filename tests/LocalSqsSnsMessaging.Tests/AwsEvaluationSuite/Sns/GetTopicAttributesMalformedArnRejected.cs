// Translated from behavioral/sns/check_get_topic_attributes_malformed_arn_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.SimpleNotificationService.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sns;

/// <summary>sns.get-topic-attributes-malformed-arn-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class GetTopicAttributesMalformedArnRejected
{
    private const string Check = "get-topic-attributes-malformed-arn-rejected";

    /// <summary>
    /// GetTopicAttributes must reject a malformed TopicArn with InvalidParameter (not
    /// NotFound), never leaking any attributes; a well-formed ARN for the same topic still
    /// resolves correctly, both before and after the bad call.
    /// </summary>
    [Test]
    [DisplayName("sns.get-topic-attributes-malformed-arn-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    [KnownFidelityGap(FidelityGap.SnsValidation, "GetTopicAttributes with TopicArn 'this-is-not-an-arn' fails with an ArgumentException (HTTP 500) instead of InvalidParameter 'An ARN must have at least 6 elements, not 1'")]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ev = ctx.Sns;
        List<Amazon.SimpleNotificationService.Model.Tag> tagSet =
            [new() { Key = "sim2real", Value = Check }, new() { Key = "sim2real:managed", Value = "true" }];
        var topicArn = (await ev.CreateTopicAsync(new CreateTopicRequest { Name = FidelityContext.UniqueName("sim2real-behav-gta-arn-"), Tags = tagSet })).TopicArn;
        ctx.OnCleanup(() => ev.DeleteTopicAsync(new DeleteTopicRequest { TopicArn = topicArn }));

        // Positive control: a well-formed ARN resolves attributes.
        var before = (await ev.GetTopicAttributesAsync(new GetTopicAttributesRequest { TopicArn = topicArn })).Attributes!;
        Ensure(before["TopicArn"] == topicArn,
            $"expected positive control to resolve '{topicArn}', got '{before.GetValueOrDefault("TopicArn")}'");

        // Break exactly one thing: a TopicArn that isn't even ARN-shaped.
        const string malformedArn = "this-is-not-an-arn";
        Dictionary<string, string>? result = null;
        var (code, message) = await Error(async () =>
        {
            var resp = await ev.GetTopicAttributesAsync(new GetTopicAttributesRequest { TopicArn = malformedArn });
            result = resp.Attributes;
            return resp;
        });
        Ensure(code == "InvalidParameter", $"expected InvalidParameter for a malformed TopicArn, got {code}");
        const string expected = "Invalid parameter: TopicArn Reason: An ARN must have at least 6 elements, not 1";
        Ensure(message == expected, $"expected message '{expected}', got '{message}'");
        // Effect absent: nothing was returned by the rejected call.
        Ensure(result is null, $"rejected call leaked attributes: {result}");

        // The rejected call left service state untouched: the valid ARN still resolves.
        var after = (await ev.GetTopicAttributesAsync(new GetTopicAttributesRequest { TopicArn = topicArn })).Attributes!;
        Ensure(after["TopicArn"] == topicArn,
            $"expected valid ARN to still resolve after the rejected call, got '{after.GetValueOrDefault("TopicArn")}'");
    }

    /// <summary>
    /// Call fn(); return (code, message) of the ClientError it raises. An unexpected success
    /// raises, so a target that accepts what AWS refuses never reads as a code mismatch.
    /// </summary>
    private static async Task<(string? Code, string? Message)> Error<T>(Func<Task<T>> fn)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is null)
        {
            throw Fail($"expected the call to be refused, but it succeeded and returned {result}");
        }

        return (error.Code, error.Message);
    }
}
