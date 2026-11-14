// Translated from behavioral/sns/check_topic_arn_of_another_region_rejected_by_a_regional_endpoint.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.SimpleNotificationService.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sns;

/// <summary>sns.topic-arn-of-another-region-rejected-by-a-regional-endpoint</summary>
[Category("AwsEvaluationSuite")]
public sealed class TopicArnOfAnotherRegionRejectedByARegionalEndpoint
{
    private const string Check = "topic-arn-of-another-region-rejected-by-a-regional-endpoint";

    /// <summary>
    /// Another region's SNS endpoint refuses a topic's ARN for GetTopicAttributes, Publish and
    /// Subscribe (creating no subscription), while the topic's own region serves all three calls,
    /// so the refusal is the region boundary and not a missing topic.
    /// </summary>
    [Test]
    [DisplayName("sns.topic-arn-of-another-region-rejected-by-a-regional-endpoint")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    [KnownFidelityGap(FidelityGap.SnsRegions, "SNS isn't regional: an endpoint in one region answers for a topic ARN in another")]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var home = ctx.Region;
        var away = OtherRegion(home);
        var sns = ctx.Sns;
        var snsAway = ctx.SnsClient(away);
        var topic = (await sns.CreateTopicAsync(new CreateTopicRequest
        {
            Name = FidelityContext.UniqueName("sim2real-behav-sns-xrarn-"),
            Tags = [new() { Key = "sim2real", Value = Check }],
        })).TopicArn;
        ctx.OnCleanup(() => sns.DeleteTopicAsync(new DeleteTopicRequest { TopicArn = topic }));
        var queueArn = $"arn:aws:sqs:{away}:{ctx.Account}:sim2real-behav-sns-xrarn-{Guid.NewGuid().ToString("N")[..8]}";

        var (code, message) = await Error(() => snsAway.GetTopicAttributesAsync(new GetTopicAttributesRequest { TopicArn = topic }));
        Ensure((code, message) == ("InvalidParameter", "Invalid parameter: TopicArn"),
            $"GetTopicAttributes in {away} for a {home} topic gave '{code}': '{message}'");
        (code, message) = await Error(() => snsAway.PublishAsync(new PublishRequest { TopicArn = topic, Message = "sim2real cross-region" }));
        Ensure((code, message) == ("InvalidParameter", "Invalid parameter: TopicArn"),
            $"Publish in {away} to a {home} topic gave '{code}': '{message}'");
        (code, message) = await Error(() => snsAway.SubscribeAsync(new SubscribeRequest { TopicArn = topic, Protocol = "sqs", Endpoint = queueArn }));
        Ensure((code, message) == ("InvalidParameter", "Invalid parameter: TopicArn"),
            $"Subscribe in {away} to a {home} topic gave '{code}': '{message}'");
        var subs = new List<Subscription>();
        await foreach (var page in sns.Paginators.ListSubscriptionsByTopic(new ListSubscriptionsByTopicRequest { TopicArn = topic }).Responses)
        {
            subs.AddRange(page.Subscriptions ?? []);
        }

        Ensure(subs.Count == 0, $"the refused cross-region Subscribe left {subs.Count} subscription(s) on the topic");

        // Positive control: the topic's own region serves the same calls.
        var attrs = (await sns.GetTopicAttributesAsync(new GetTopicAttributesRequest { TopicArn = topic })).Attributes ?? [];
        Ensure(attrs.GetValueOrDefault("TopicArn") == topic, "the home region does not serve its own topic");
        Ensure(!string.IsNullOrEmpty((await sns.PublishAsync(new PublishRequest { TopicArn = topic, Message = "sim2real home region" })).MessageId),
            "Publish in the topic's own region returned no MessageId");
        var sub = (await sns.SubscribeAsync(new SubscribeRequest
        {
            TopicArn = topic, Protocol = "sqs", Endpoint = queueArn, ReturnSubscriptionArn = true,
        })).SubscriptionArn;
        ctx.OnCleanup(() => sns.UnsubscribeAsync(new UnsubscribeRequest { SubscriptionArn = sub }));
        Ensure(sub.StartsWith(topic + ":", StringComparison.Ordinal), "the home-region Subscribe did not return a subscription of the topic");
    }

    /// <summary>A second commercial region of the same account (region-pinned by design).</summary>
    private static string OtherRegion(string region) => region != "us-east-1" ? "us-east-1" : "us-west-2";

    /// <summary>(Code, Message) of the error <c>fn</c> raises; an unexpected success raises, naming it.</summary>
    private static async Task<(string? Code, string? Message)> Error<T>(Func<Task<T>> fn)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is null)
        {
            throw Fail($"expected the call to be refused, but it succeeded: {result}");
        }

        return (error.Code, error.Message);
    }
}
