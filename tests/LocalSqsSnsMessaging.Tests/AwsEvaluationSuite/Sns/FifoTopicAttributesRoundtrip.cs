// Translated from behavioral/sns/check_fifo_topic_attributes_roundtrip.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.SimpleNotificationService;
using Amazon.SimpleNotificationService.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sns;

/// <summary>sns.fifo-topic-attributes-roundtrip</summary>
[Category("AwsEvaluationSuite")]
public sealed class FifoTopicAttributesRoundtrip
{
    private const string Check = "fifo-topic-attributes-roundtrip";

    /// <summary>CreateTopic + register best-effort DeleteTopic cleanup; returns the TopicArn.</summary>
    private static async Task<string> CreateTopic(AmazonSimpleNotificationServiceClient client, FidelityContext ctx, string name,
        Dictionary<string, string>? attributes = null, List<Tag>? tags = null)
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

    private static string Py(string? value) => value ?? "None";

    /// <summary>
    /// A FIFO topic reports FifoTopic=true, defaults ContentBasedDeduplication to false, and that
    /// attribute round-trips through SetTopicAttributes independently of CreateTopic.
    /// </summary>
    [Test]
    [DisplayName("sns.fifo-topic-attributes-roundtrip")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    [KnownFidelityGap(FidelityGap.SnsTopicAttributes, "GetTopicAttributes on a FIFO topic omits ContentBasedDeduplication instead of reporting its default 'false'")]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ev = ctx.Sns;
        var name = FidelityContext.UniqueName("sim2real-behav-sns-fifo-ok-") + ".fifo";
        var arn = await CreateTopic(
            ev, ctx, name, attributes: new() { ["FifoTopic"] = "true" },
            tags: [new() { Key = "sim2real", Value = Check }]);
        var attrs = (await ev.GetTopicAttributesAsync(new GetTopicAttributesRequest { TopicArn = arn })).Attributes ?? [];
        Ensure(attrs.GetValueOrDefault("FifoTopic") == "true", $"expected FifoTopic=true, got {Py(attrs.GetValueOrDefault("FifoTopic"))}");
        Ensure(attrs.GetValueOrDefault("ContentBasedDeduplication") == "false",
            $"expected ContentBasedDeduplication default false, got {Py(attrs.GetValueOrDefault("ContentBasedDeduplication"))}");

        await ev.SetTopicAttributesAsync(new SetTopicAttributesRequest { TopicArn = arn, AttributeName = "ContentBasedDeduplication", AttributeValue = "true" });
        var turnedOn = (await ev.GetTopicAttributesAsync(new GetTopicAttributesRequest { TopicArn = arn })).Attributes ?? [];
        Ensure(turnedOn.GetValueOrDefault("ContentBasedDeduplication") == "true",
            $"expected ContentBasedDeduplication=true after SetTopicAttributes, got {Py(turnedOn.GetValueOrDefault("ContentBasedDeduplication"))}");

        await ev.SetTopicAttributesAsync(new SetTopicAttributesRequest { TopicArn = arn, AttributeName = "ContentBasedDeduplication", AttributeValue = "false" });
        var turnedOff = (await ev.GetTopicAttributesAsync(new GetTopicAttributesRequest { TopicArn = arn })).Attributes ?? [];
        Ensure(turnedOff.GetValueOrDefault("ContentBasedDeduplication") == "false",
            $"expected ContentBasedDeduplication=false after resetting via SetTopicAttributes, got {Py(turnedOff.GetValueOrDefault("ContentBasedDeduplication"))}");
    }
}
