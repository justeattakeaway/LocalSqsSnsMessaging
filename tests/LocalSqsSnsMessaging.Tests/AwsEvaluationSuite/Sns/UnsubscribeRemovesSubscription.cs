// Translated from behavioral/sns/check_unsubscribe_removes_subscription.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Text.Json.Nodes;
using Amazon.SimpleNotificationService;
using Amazon.SimpleNotificationService.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sns;

/// <summary>sns.unsubscribe-removes-subscription</summary>
[Category("AwsEvaluationSuite")]
public sealed class UnsubscribeRemovesSubscription
{
    /// <summary>
    /// Unsubscribe deletes the subscription: GetSubscriptionAttributes afterward returns NotFound,
    /// the dead ARN no longer appears in the topic's subscription list, and a further mutation on that
    /// same ARN (SetSubscriptionAttributes) is rejected too — not just the read.
    /// </summary>
    [Test]
    [DisplayName("sns.unsubscribe-removes-subscription")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ev = ctx.Sns;
        var topicArn = await CreateTopic(ev, ctx, FidelityContext.UniqueName("sim2real-behav-sns-unsub-"),
            tags: [new() { Key = "sim2real", Value = "sns-unsubscribe" }, new() { Key = "sim2real:managed", Value = "true" }]);
        var subArn = await SubscribeFakeQueue(ev, ctx, topicArn);
        await ev.UnsubscribeAsync(new UnsubscribeRequest { SubscriptionArn = subArn });

        var (result, error) = await FidelityContext.Capture(() => ev.GetSubscriptionAttributesAsync(new GetSubscriptionAttributesRequest { SubscriptionArn = subArn }));
        if (error is null)
        {
            throw Fail($"GetSubscriptionAttributes after unsubscribe SUCCEEDED: {result}");
        }

        var (code, message) = (error.Code, error.Message);
        Ensure(code == "NotFound", $"expected NotFound after unsubscribe, got {code}");
        Ensure(message == "Subscription does not exist", $"unexpected NotFound message after unsubscribe: '{message}'");

        var remaining = ((await ev.ListSubscriptionsByTopicAsync(new ListSubscriptionsByTopicRequest { TopicArn = topicArn })).Subscriptions ?? [])
            .Select(s => s.SubscriptionArn).ToList();
        Ensure(!remaining.Contains(subArn),
            $"unsubscribed ARN '{subArn}' is still listed on the topic: [{string.Join(", ", remaining.Select(r => $"'{r}'"))}]");

        var (setResult, setError) = await FidelityContext.Capture(() => ev.SetSubscriptionAttributesAsync(new SetSubscriptionAttributesRequest
        {
            SubscriptionArn = subArn, AttributeName = "RawMessageDelivery", AttributeValue = "true",
        }));
        if (setError is null)
        {
            throw Fail($"SetSubscriptionAttributes on the removed ARN SUCCEEDED: {setResult}");
        }

        var (setCode, setMessage) = (setError.Code, setError.Message);
        Ensure(setCode == "NotFound", $"expected SetSubscriptionAttributes on the removed ARN to be rejected, got {setCode}");
        Ensure(setMessage == "Subscription does not exist",
            $"unexpected NotFound message for SetSubscriptionAttributes on the removed ARN: '{setMessage}'");
    }

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

    /// <summary>A well-formed SQS queue ARN in the topic's account/region (SNS confirms sqs subs without verifying the queue exists).</summary>
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
