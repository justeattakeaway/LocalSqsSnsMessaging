// Translated from behavioral/sns/check_subscribe_sqs_auto_confirms.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Text.Json.Nodes;
using Amazon.SimpleNotificationService;
using Amazon.SimpleNotificationService.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sns;

/// <summary>sns.subscribe-sqs-auto-confirms</summary>
[Category("AwsEvaluationSuite")]
public sealed class SubscribeSqsAutoConfirms
{
    private const string Check = "subscribe-sqs-auto-confirms";

    /// <summary>Subscribing an sqs-protocol endpoint auto-confirms immediately (no PendingConfirmation).</summary>
    [Test]
    [DisplayName("sns.subscribe-sqs-auto-confirms")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    [KnownFidelityGap(FidelityGap.SnsSubscriptions, "Subscribe to an sqs endpoint whose queue does not exist is refused with NotFound 'Queue not found.'; AWS accepts it without checking the queue")]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ev = ctx.Sns;
        var topicArn = await CreateTopic(ev, ctx, FidelityContext.UniqueName("sim2real-behav-sns-subconfirm-"),
            tags: [new() { Key = "sim2real", Value = Check }]);
        var subArn = await SubscribeFakeQueue(ev, ctx, topicArn);
        Ensure(!string.IsNullOrEmpty(subArn) && subArn != "pending confirmation",
            $"expected an auto-confirmed SubscriptionArn, got '{subArn}'");
        Ensure(subArn.StartsWith(topicArn + ":", StringComparison.Ordinal),
            $"expected SubscriptionArn to be derived from the topic ARN, got '{subArn}'");
        var attrs = (await ev.GetSubscriptionAttributesAsync(new GetSubscriptionAttributesRequest { SubscriptionArn = subArn })).Attributes ?? [];
        Ensure(attrs.GetValueOrDefault("PendingConfirmation") == "false",
            $"expected PendingConfirmation=false for sqs protocol, got {attrs.GetValueOrDefault("PendingConfirmation")}");
        Ensure(attrs.GetValueOrDefault("Protocol") == "sqs", $"expected Protocol=sqs, got {attrs.GetValueOrDefault("Protocol")}");
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
