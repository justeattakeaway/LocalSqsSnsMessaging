// Translated from behavioral/sns/check_set_topic_attributes_unknown_name_fails.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.SimpleNotificationService.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sns;

/// <summary>sns.set-topic-attributes-unknown-name-fails</summary>
[Category("AwsEvaluationSuite")]
public sealed class SetTopicAttributesUnknownNameFails
{
    /// <summary>
    /// [overly-permissive] SetTopicAttributes with an unknown AttributeName must be rejected,
    /// must not affect the topic's existing attributes, and a well-formed SetTopicAttributes call
    /// on the same topic must still succeed (positive control).
    /// </summary>
    [Test]
    [DisplayName("sns.set-topic-attributes-unknown-name-fails")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    [KnownFidelityGap(FidelityGap.SnsTopicAttributes, "A new topic reports no default DisplayName attribute, so the baseline read fails before the unknown attribute is tried")]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sns = ctx.Sns;
        var arn = await CreateTopic(ctx);

        var originalDisplayName = (await sns.GetTopicAttributesAsync(new GetTopicAttributesRequest { TopicArn = arn })).Attributes["DisplayName"];

        var (code, message) = await Error(() => sns.SetTopicAttributesAsync(new SetTopicAttributesRequest
        {
            TopicArn = arn, AttributeName = "NotARealAttribute", AttributeValue = "x",
        }));
        Ensure(code == "InvalidParameter", $"expected InvalidParameter, got '{code}'");
        Ensure(message == "Invalid parameter: AttributeName", $"expected the exact message 'Invalid parameter: AttributeName', got '{message}'");

        var unchangedDisplayName = (await sns.GetTopicAttributesAsync(new GetTopicAttributesRequest { TopicArn = arn })).Attributes["DisplayName"];
        Ensure(unchangedDisplayName == originalDisplayName, "topic attributes mutated despite rejected SetTopicAttributes call");

        // positive control: a well-formed attribute update on the same topic still succeeds
        var displayName = FidelityContext.UniqueName("sim2real-displayname-");
        await sns.SetTopicAttributesAsync(new SetTopicAttributesRequest { TopicArn = arn, AttributeName = "DisplayName", AttributeValue = displayName });
        var fetchedDisplayName = (await sns.GetTopicAttributesAsync(new GetTopicAttributesRequest { TopicArn = arn })).Attributes["DisplayName"];
        Ensure(fetchedDisplayName == displayName, $"expected DisplayName '{displayName}', got '{fetchedDisplayName}'");
    }

    /// <summary>
    /// Call fn(); return the botocore error (Code, Message) if a ClientError was raised. An unexpected
    /// SUCCESS raises.
    /// </summary>
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

    /// <summary>Create a topic, register its teardown, and return its ARN.</summary>
    private static async Task<string> CreateTopic(FidelityContext ctx, string? name = null)
    {
        var sns = ctx.Sns;
        name ??= FidelityContext.UniqueName("sim2real-behav-sns-topic-");
        var arn = (await sns.CreateTopicAsync(new CreateTopicRequest { Name = name })).TopicArn;
        ctx.OnCleanup(() => sns.DeleteTopicAsync(new DeleteTopicRequest { TopicArn = arn }));
        return arn;
    }
}
