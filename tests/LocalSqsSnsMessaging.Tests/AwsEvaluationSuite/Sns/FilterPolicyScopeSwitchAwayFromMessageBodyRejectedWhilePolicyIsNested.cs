// Translated from behavioral/sns/check_filter_policy_scope_switch_away_from_message_body_rejected_while_policy_is_nested.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Text.Json.Nodes;
using Amazon.SimpleNotificationService.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sns;

/// <summary>sns.filter-policy-scope-switch-away-from-message-body-rejected-while-policy-is-nested</summary>
[Category("AwsEvaluationSuite")]
public sealed class FilterPolicyScopeSwitchAwayFromMessageBodyRejectedWhilePolicyIsNested
{
    private const string Check = "filter-policy-scope-switch-away-from-message-body-rejected-while-policy-is-nested";

    /// <summary>
    /// Switching FilterPolicyScope to MessageAttributes while a nested policy is stored is
    /// refused with InvalidParameter and its exact message, leaving scope and policy unchanged;
    /// the same switch succeeds once the stored policy is flat.
    /// </summary>
    [Test]
    [DisplayName("sns.filter-policy-scope-switch-away-from-message-body-rejected-while-policy-is-nested")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    [KnownFidelityGap(FidelityGap.SnsFilterPolicy, "switching a nested policy to MessageAttributes scope is refused with 'Invalid parameter: FilterPolicy: Filter policy scope ...' instead of 'Invalid parameter: Filter policy scope ...'")]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sns = ctx.Sns;
        var topicArn = await CreateTopic(ctx);
        var subArn = await SubscribeFakeQueue(ctx, topicArn);

        const string nested = """{"store": {"city": ["seattle"]}}""";
        await SetAttribute(sns, subArn, "FilterPolicyScope", "MessageBody");
        await SetAttribute(sns, subArn, "FilterPolicy", nested);
        var attrs = await GetAttributes(sns, subArn);
        Ensure(attrs["FilterPolicyScope"] == "MessageBody",
            $"setup must leave FilterPolicyScope=MessageBody, got '{attrs["FilterPolicyScope"]}'");
        Ensure(JsonEquals(attrs["FilterPolicy"], nested),
            $"setup must leave the nested policy stored, got '{attrs["FilterPolicy"]}'");

        var (code, msg) = await Error(() => sns.SetSubscriptionAttributesAsync(new SetSubscriptionAttributesRequest
        {
            SubscriptionArn = subArn,
            AttributeName = "FilterPolicyScope",
            AttributeValue = "MessageAttributes",
        }));
        Ensure(code == "InvalidParameter",
            $"expected InvalidParameter switching scope away while the policy is nested, got {code}");
        const string expected = "Invalid parameter: Filter policy scope MessageAttributes does not support " +
            "nested filter policy";
        Ensure(msg == expected, $"expected message '{expected}', got '{msg}'");

        var after = await GetAttributes(sns, subArn);
        Ensure(after["FilterPolicyScope"] == "MessageBody",
            $"a refused scope switch must leave FilterPolicyScope alone, got '{after["FilterPolicyScope"]}'");
        Ensure(JsonEquals(after["FilterPolicy"], nested),
            $"a refused scope switch must leave FilterPolicy alone, got '{after["FilterPolicy"]}'");

        // A flat policy is valid under both scopes, so the same switch must now succeed.
        const string flat = """{"event": ["created"]}""";
        await SetAttribute(sns, subArn, "FilterPolicy", flat);
        await SetAttribute(sns, subArn, "FilterPolicyScope", "MessageAttributes");
        var final = await GetAttributes(sns, subArn);
        Ensure(final["FilterPolicyScope"] == "MessageAttributes",
            $"the switch must succeed once the policy is flat, got '{final["FilterPolicyScope"]}'");
        Ensure(JsonEquals(final["FilterPolicy"], flat),
            $"the flat policy must survive the successful switch, got '{final["FilterPolicy"]}'");
    }

    private static bool JsonEquals(string a, string b) => JsonNode.DeepEquals(JsonNode.Parse(a), JsonNode.Parse(b));

    private static Task SetAttribute(Amazon.SimpleNotificationService.AmazonSimpleNotificationServiceClient sns, string subArn, string name, string value) =>
        sns.SetSubscriptionAttributesAsync(new SetSubscriptionAttributesRequest { SubscriptionArn = subArn, AttributeName = name, AttributeValue = value });

    private static async Task<Dictionary<string, string>> GetAttributes(Amazon.SimpleNotificationService.AmazonSimpleNotificationServiceClient sns, string subArn) =>
        (await sns.GetSubscriptionAttributesAsync(new GetSubscriptionAttributesRequest { SubscriptionArn = subArn })).Attributes;

    /// <summary>CreateTopic (tagged + prefix-named) and register DeleteTopic immediately.</summary>
    private static async Task<string> CreateTopic(FidelityContext ctx)
    {
        var sns = ctx.Sns;
        var name = FidelityContext.UniqueName("sim2real-behav-sns-t-");
        var arn = (await sns.CreateTopicAsync(new CreateTopicRequest { Name = name, Tags = [new() { Key = "sim2real", Value = Check }] })).TopicArn;
        ctx.OnCleanup(() => ctx.Sns.DeleteTopicAsync(new DeleteTopicRequest { TopicArn = arn }));
        return arn;
    }

    /// <summary>
    /// Subscribe a never-created SQS queue ARN and register Unsubscribe.
    /// SNS confirms sqs subscriptions without verifying that the queue exists.
    /// </summary>
    private static async Task<string> SubscribeFakeQueue(FidelityContext ctx, string topicArn)
    {
        var sns = ctx.Sns;
        var parts = topicArn.Split(':');
        var name = FidelityContext.UniqueName("sim2real-behav-sns-q-");
        var queueArn = $"arn:aws:sqs:{parts[3]}:{parts[4]}:{name}";
        var subArn = (await sns.SubscribeAsync(new SubscribeRequest
        {
            TopicArn = topicArn,
            Protocol = "sqs",
            Endpoint = queueArn,
            ReturnSubscriptionArn = true,
        })).SubscriptionArn;
        ctx.OnCleanup(() => ctx.Sns.UnsubscribeAsync(new UnsubscribeRequest { SubscriptionArn = subArn }));
        return subArn;
    }

    /// <summary>(Error.Code, Error.Message) the call raises. An unexpected success RAISES.</summary>
    private static async Task<(string Code, string Message)> Error<T>(Func<Task<T>> fn)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is null)
        {
            throw Fail($"expected the call to be refused, but it succeeded: {result}");
        }

        return (error.Code ?? "", error.Message ?? "");
    }
}
