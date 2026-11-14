// Translated from behavioral/sns/check_filter_policy_message_body_scope_counts_nested_leaf_keys_toward_the_key_limit.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Text.Json.Nodes;
using Amazon.SimpleNotificationService.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sns;

/// <summary>sns.filter-policy-message-body-scope-counts-nested-leaf-keys-toward-the-key-limit</summary>
[Category("AwsEvaluationSuite")]
public sealed class FilterPolicyMessageBodyScopeCountsNestedLeafKeysTowardTheKeyLimit
{
    private const string Check = "filter-policy-message-body-scope-counts-nested-leaf-keys-toward-the-key-limit";

    private const string Five = """{"order": {"customer": {"tier": ["gold"], "region": ["eu"]}, "total": [{"numeric": [">", 0]}]}, "source": ["web"], "kind": ["created"]}""";
    private const string Six = """{"order": {"customer": {"tier": ["gold"], "region": ["eu"], "vip": ["yes"]}, "total": [{"numeric": [">", 0]}]}, "source": ["web"], "kind": ["created"]}""";

    /// <summary>
    /// Under FilterPolicyScope=MessageBody the five-key limit counts nested leaf keys: five leaves
    /// under three top-level keys are accepted, a sixth leaf is refused and the stored policy kept.
    /// </summary>
    [Test]
    [DisplayName("sns.filter-policy-message-body-scope-counts-nested-leaf-keys-toward-the-key-limit")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sns = ctx.Sns;
        var topic = (await sns.CreateTopicAsync(new CreateTopicRequest
        {
            Name = FidelityContext.UniqueName("sim2real-behav-sns-fpleaf-"),
            Tags = [new() { Key = "sim2real", Value = Check }],
        })).TopicArn;
        ctx.OnCleanup(() => sns.DeleteTopicAsync(new DeleteTopicRequest { TopicArn = topic }));
        var (region, account) = (topic.Split(':')[3], topic.Split(':')[4]);
        var endpoint = $"arn:aws:sqs:{region}:{account}:sim2real-behav-sns-fpleaf-{Guid.NewGuid().ToString("N")[..8]}";
        var sub = (await sns.SubscribeAsync(new SubscribeRequest
        {
            TopicArn = topic,
            Protocol = "sqs",
            Endpoint = endpoint,
            ReturnSubscriptionArn = true,
            Attributes = new() { ["FilterPolicyScope"] = "MessageBody", ["FilterPolicy"] = Five },
        })).SubscriptionArn;
        ctx.OnCleanup(() => sns.UnsubscribeAsync(new UnsubscribeRequest { SubscriptionArn = sub }));
        var attrs = (await sns.GetSubscriptionAttributesAsync(new GetSubscriptionAttributesRequest { SubscriptionArn = sub })).Attributes ?? [];
        Ensure(attrs.GetValueOrDefault("FilterPolicyScope") == "MessageBody"
                && JsonNode.DeepEquals(JsonNode.Parse(attrs.GetValueOrDefault("FilterPolicy", "null")), JsonNode.Parse(Five)),
            $"a five-leaf MessageBody policy must be accepted and stored; scope '{attrs.GetValueOrDefault("FilterPolicyScope")}'");

        var (code, message) = await Error(() => sns.SetSubscriptionAttributesAsync(new SetSubscriptionAttributesRequest
        {
            SubscriptionArn = sub,
            AttributeName = "FilterPolicy",
            AttributeValue = Six,
        }));
        Ensure((code, message) == ("InvalidParameter", "Invalid parameter: FilterPolicy: Filter policy can not have more than 5 keys"),
            $"a six-leaf MessageBody policy gave '{code}': '{message}'");
        var stored = JsonNode.Parse((await sns.GetSubscriptionAttributesAsync(new GetSubscriptionAttributesRequest { SubscriptionArn = sub })).Attributes["FilterPolicy"]);
        Ensure(JsonNode.DeepEquals(stored, JsonNode.Parse(Five)), "the refused six-leaf policy replaced the stored five-leaf policy");
    }

    /// <summary>(Code, Message) of the error fn raises; an unexpected success raises, naming it.</summary>
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
