// Translated from behavioral/sns/check_subscription_created_with_attributes_roundtrip.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Text.Json.Nodes;
using Amazon.SimpleNotificationService;
using Amazon.SimpleNotificationService.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sns;

/// <summary>sns.subscription-created-with-attributes-roundtrip</summary>
[Category("AwsEvaluationSuite")]
public sealed class SubscriptionCreatedWithAttributesRoundtrip
{
    private const string Check = "subscription-created-with-attributes-roundtrip";

    /// <summary>Attributes supplied at Subscribe time are persisted and readable on the subscription.</summary>
    [Test]
    [DisplayName("sns.subscription-created-with-attributes-roundtrip")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ev = ctx.Sns;
        var topicArn = await CreateTopic(ev, ctx, FidelityContext.UniqueName("sim2real-behav-sns-subattrs-"),
            tags: [new() { Key = "sim2real", Value = Check }, new() { Key = "sim2real:managed", Value = "true" }]);
        var subArn = await SubscribeFakeQueue(ev, ctx,
            topicArn, rawMessageDelivery: true, filterPolicy: new JsonObject { ["event"] = new JsonArray("approved") });
        Ensure((await Attributes(ev, subArn)).GetValueOrDefault("RawMessageDelivery") == "true",
            "expected RawMessageDelivery=true from Subscribe-time attributes");
        var policyJson = (await Attributes(ev, subArn)).GetValueOrDefault("FilterPolicy");
        var policy = JsonNode.Parse(string.IsNullOrEmpty(policyJson) ? "{}" : policyJson);
        Ensure(JsonNode.DeepEquals(policy, new JsonObject { ["event"] = new JsonArray("approved") }),
            $"expected filter policy round-trip, got {policy?.ToJsonString()}");
        var attrsFinal = await Attributes(ev, subArn);
        Ensure(attrsFinal.ContainsKey("FilterPolicyScope"),
            "expected FilterPolicyScope to be explicitly present once a FilterPolicy is set, " +
            $"got attributes [{string.Join(", ", attrsFinal.Keys.Order(StringComparer.Ordinal).Select(k => $"'{k}'"))}]");
        Ensure(attrsFinal["FilterPolicyScope"] == "MessageAttributes",
            $"expected default FilterPolicyScope=MessageAttributes, got '{attrsFinal["FilterPolicyScope"]}'");
    }

    private static async Task<Dictionary<string, string>> Attributes(AmazonSimpleNotificationServiceClient ev, string subArn)
        => (await ev.GetSubscriptionAttributesAsync(new GetSubscriptionAttributesRequest { SubscriptionArn = subArn })).Attributes ?? [];

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

    /// <summary>
    /// A well-formed SQS queue ARN in the topic's account/region (SNS confirms sqs subs without
    /// verifying the queue exists).
    /// </summary>
    private static string FakeQueueArn(string topicArn, string? name = null)
    {
        var parts = topicArn.Split(':');
        name ??= $"sim2real-behav-sns-q-{Guid.NewGuid().ToString("N")[..8]}";
        return $"arn:aws:sqs:{parts[3]}:{parts[4]}:{name}";
    }

    /// <summary>Subscribe an sqs-protocol endpoint; register unsubscribe cleanup; return the SubscriptionArn.</summary>
    private static async Task<string> SubscribeFakeQueue(AmazonSimpleNotificationServiceClient client, FidelityContext ctx, string topicArn,
        string? queueArn = null, bool rawMessageDelivery = false, JsonNode? filterPolicy = null, string? filterPolicyScope = null)
    {
        queueArn ??= FakeQueueArn(topicArn);
        var attributes = new Dictionary<string, string>();
        if (rawMessageDelivery)
        {
            attributes["RawMessageDelivery"] = "true";
        }

        if (filterPolicy is not null)
        {
            attributes["FilterPolicy"] = filterPolicy.ToJsonString();
        }

        if (filterPolicyScope is not null)
        {
            attributes["FilterPolicyScope"] = filterPolicyScope;
        }

        var request = new SubscribeRequest { TopicArn = topicArn, Protocol = "sqs", Endpoint = queueArn };
        if (attributes.Count > 0)
        {
            request.Attributes = attributes;
        }

        var subArn = (await client.SubscribeAsync(request)).SubscriptionArn;
        if (!string.IsNullOrEmpty(subArn) && subArn != "pending confirmation")
        {
            ctx.OnCleanup(() => client.UnsubscribeAsync(new UnsubscribeRequest { SubscriptionArn = subArn }));
        }

        return subArn;
    }
}
