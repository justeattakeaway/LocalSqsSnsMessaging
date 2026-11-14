// Translated from behavioral/sns/check_topic_set_attribute_immutable_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.SimpleNotificationService;
using Amazon.SimpleNotificationService.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sns;

/// <summary>sns.topic-set-attribute-immutable-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class TopicSetAttributeImmutableRejected
{
    private const string Check = "topic-set-attribute-immutable-rejected";

    /// <summary>
    /// SetTopicAttributes rejects attempts to flip FifoTopic post-creation, leaves the
    /// attribute unchanged, and a sibling SetTopicAttributes call for a different, mutable
    /// attribute on the same topic still succeeds (overly-permissive check).
    /// </summary>
    [Test]
    [DisplayName("sns.topic-set-attribute-immutable-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    [KnownFidelityGap(FidelityGap.SnsTopicAttributes, "SetTopicAttributes FifoTopic=true on a standard topic succeeds instead of being refused with InvalidParameter 'Invalid parameter: AttributeName'")]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ev = ctx.Sns;
        var name = FidelityContext.UniqueName("sim2real-behav-sns-immut-");
        var arn = await CreateTopic(ev, ctx, name,
            tags: [new() { Key = "sim2real", Value = Check }, new() { Key = "sim2real:managed", Value = "true" }]);

        // real SNS omits FifoTopic from GetTopicAttributes entirely for a standard topic
        // rather than reporting it as "false" -- absence means false, per the API's own docs
        var originalFifo = (await ev.GetTopicAttributesAsync(new GetTopicAttributesRequest { TopicArn = arn })).Attributes?.GetValueOrDefault("FifoTopic", "false");

        var (result, error) = await FidelityContext.Capture(() =>
            ev.SetTopicAttributesAsync(new SetTopicAttributesRequest { TopicArn = arn, AttributeName = "FifoTopic", AttributeValue = "true" }));
        if (error is null)
        {
            throw Fail($"SetTopicAttributes FifoTopic=true SUCCEEDED: {result}");
        }

        var (code, message) = (error.Code, error.Message);
        Ensure(code == "InvalidParameter", $"expected InvalidParameter when mutating immutable FifoTopic attribute, got {code}");
        Ensure(message == "Invalid parameter: AttributeName", $"unexpected refusal message when mutating FifoTopic: '{message}'");

        var unchangedFifo = (await ev.GetTopicAttributesAsync(new GetTopicAttributesRequest { TopicArn = arn })).Attributes?.GetValueOrDefault("FifoTopic", "false");
        Ensure(unchangedFifo == originalFifo,
            "FifoTopic attribute mutated despite rejected SetTopicAttributes call: " +
            $"expected '{originalFifo}', got '{unchangedFifo}'");

        // positive control: a well-formed attribute update on the same topic still succeeds
        var displayName = FidelityContext.UniqueName("sim2real-displayname-");
        await ev.SetTopicAttributesAsync(new SetTopicAttributesRequest { TopicArn = arn, AttributeName = "DisplayName", AttributeValue = displayName });
        var fetchedDisplayName = (await ev.GetTopicAttributesAsync(new GetTopicAttributesRequest { TopicArn = arn })).Attributes!["DisplayName"];
        Ensure(fetchedDisplayName == displayName, $"expected DisplayName update to persist, got '{fetchedDisplayName}'");
    }

    /// <summary>CreateTopic + register best-effort DeleteTopic cleanup; returns the TopicArn.</summary>
    private static async Task<string> CreateTopic(AmazonSimpleNotificationServiceClient client, FidelityContext ctx, string name,
        Dictionary<string, string>? attributes = null, List<Amazon.SimpleNotificationService.Model.Tag>? tags = null)
    {
        var request = new CreateTopicRequest { Name = name };
        if (attributes is { Count: > 0 })
        {
            request.Attributes = attributes;
        }

        if (tags is { Count: > 0 })
        {
            request.Tags = tags;
        }

        var arn = (await client.CreateTopicAsync(request)).TopicArn;
        ctx.OnCleanup(() => client.DeleteTopicAsync(new DeleteTopicRequest { TopicArn = arn }));
        return arn;
    }
}
