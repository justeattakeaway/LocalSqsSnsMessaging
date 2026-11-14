// Translated from behavioral/sns/check_subscription_filter_policy_update_and_validation.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Text.Json.Nodes;
using Amazon.SimpleNotificationService;
using Amazon.SimpleNotificationService.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sns;

/// <summary>sns.subscription-filter-policy-update-and-validation</summary>
[Category("AwsEvaluationSuite")]
public sealed class SubscriptionFilterPolicyUpdateAndValidation
{
    private const string Scenario = "sns-subscription-filter-policy-update-and-validation";

    /// <summary>FilterPolicy can be replaced in place; structurally invalid policies are rejected.</summary>
    [Test]
    [DisplayName("sns.subscription-filter-policy-update-and-validation")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    [KnownFidelityGap(FidelityGap.SnsFilterPolicy, "a malformed filter policy is refused with a different message from SNS's")]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ev = ctx.Sns;
        var topicArn = await CreateTopic(ev, ctx, FidelityContext.UniqueName("sim2real-behav-sns-filter-"));
        var subArn = await SubscribeFakeQueue(ev, ctx, topicArn, filterPolicy: """{"event": ["created"]}""");
        await ev.SetSubscriptionAttributesAsync(new SetSubscriptionAttributesRequest
        {
            SubscriptionArn = subArn,
            AttributeName = "FilterPolicy",
            AttributeValue = """{"event": ["updated", "deleted"]}""",
        });
        var expected = JsonNode.Parse("""{"event": ["updated", "deleted"]}""");
        var policy = JsonNode.Parse(await FilterPolicy(ev, subArn));
        Ensure(JsonNode.DeepEquals(policy, expected), $"expected updated filter policy, got {policy?.ToJsonString()}");

        var (result, error) = await FidelityContext.Capture(() => ev.SetSubscriptionAttributesAsync(new SetSubscriptionAttributesRequest
        {
            SubscriptionArn = subArn,
            AttributeName = "FilterPolicy",
            AttributeValue = """{"event": "must-be-a-list"}""",
        }));
        if (error is null)
        {
            throw Fail($"SetSubscriptionAttributes with a malformed FilterPolicy SUCCEEDED: {result}");
        }

        var (code, message) = (error.Code, error.Message);
        Ensure(code == "InvalidParameter", $"expected InvalidParameter for a malformed FilterPolicy, got {code}");
        Ensure(message ==
            "Invalid parameter: FilterPolicy: \"event\" must be an object or an array\n" +
            " at [Source: (String)\"{\"event\":\"must-be-a-list\"}\"; line: 1, column: 11]",
            $"unexpected refusal message for a malformed FilterPolicy: '{message}'");
        var policyAfter = JsonNode.Parse(await FilterPolicy(ev, subArn));
        Ensure(JsonNode.DeepEquals(policyAfter, expected), $"expected filter policy unchanged after rejected update, got {policyAfter?.ToJsonString()}");
    }

    private static async Task<string> FilterPolicy(AmazonSimpleNotificationServiceClient ev, string subArn)
    {
        var value = (await ev.GetSubscriptionAttributesAsync(new GetSubscriptionAttributesRequest { SubscriptionArn = subArn })).Attributes?.GetValueOrDefault("FilterPolicy");
        return string.IsNullOrEmpty(value) ? "{}" : value;
    }

    private static List<Amazon.SimpleNotificationService.Model.Tag> TagsKv() =>
        [new() { Key = "sim2real", Value = Scenario }, new() { Key = "sim2real:managed", Value = "true" }];

    /// <summary>CreateTopic (tagged) + register best-effort DeleteTopic cleanup; returns the TopicArn.</summary>
    private static async Task<string> CreateTopic(AmazonSimpleNotificationServiceClient client, FidelityContext ctx, string name)
    {
        var arn = (await client.CreateTopicAsync(new CreateTopicRequest { Name = name, Tags = TagsKv() })).TopicArn;
        ctx.OnCleanup(() => client.DeleteTopicAsync(new DeleteTopicRequest { TopicArn = arn }));
        return arn;
    }

    /// <summary>A well-formed SQS queue ARN in the topic's account/region (SNS confirms sqs subs without verifying the queue exists).</summary>
    private static string FakeQueueArn(string topicArn, string? name = null)
    {
        var parts = topicArn.Split(':');
        name ??= $"sim2real-behav-sns-q-{Guid.NewGuid().ToString("N")[..8]}";
        return $"arn:aws:sqs:{parts[3]}:{parts[4]}:{name}";
    }

    /// <summary>Subscribe an sqs-protocol endpoint; register unsubscribe cleanup; return the SubscriptionArn.</summary>
    private static async Task<string> SubscribeFakeQueue(AmazonSimpleNotificationServiceClient client, FidelityContext ctx, string topicArn,
        string? queueArn = null, string? filterPolicy = null)
    {
        queueArn ??= FakeQueueArn(topicArn);
        var request = new SubscribeRequest { TopicArn = topicArn, Protocol = "sqs", Endpoint = queueArn };
        if (filterPolicy is not null)
        {
            request.Attributes = new() { ["FilterPolicy"] = filterPolicy };
        }

        var subArn = (await client.SubscribeAsync(request)).SubscriptionArn;
        if (!string.IsNullOrEmpty(subArn) && subArn != "pending confirmation")
        {
            ctx.OnCleanup(() => client.UnsubscribeAsync(new UnsubscribeRequest { SubscriptionArn = subArn }));
        }

        return subArn;
    }
}
