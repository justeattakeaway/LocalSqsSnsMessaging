// Translated from behavioral/sns/check_subscription_delivery_policy_reads_back_normalized_on_an_sqs_subscription.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Text.Json.Nodes;
using Amazon.SimpleNotificationService.Model;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sns;

/// <summary>sns.subscription-delivery-policy-reads-back-normalized-on-an-sqs-subscription</summary>
[Category("AwsEvaluationSuite")]
public sealed class SubscriptionDeliveryPolicyReadsBackNormalizedOnAnSqsSubscription
{
    private static JsonObject Sent() => new()
    {
        ["healthyRetryPolicy"] = new JsonObject { ["numRetries"] = 5, ["minDelayTarget"] = 1, ["maxDelayTarget"] = 10 },
    };

    private static JsonObject Stored() => new()
    {
        ["healthyRetryPolicy"] = new JsonObject
        {
            ["minDelayTarget"] = 1, ["maxDelayTarget"] = 10, ["numRetries"] = 5, ["numMaxDelayRetries"] = null,
            ["numNoDelayRetries"] = null, ["numMinDelayRetries"] = null, ["backoffFunction"] = null,
        },
        ["sicklyRetryPolicy"] = null,
        ["throttlePolicy"] = null,
        ["requestPolicy"] = null,
        ["guaranteed"] = false,
    };

    /// <summary>
    /// A subscription DeliveryPolicy set on an SQS subscription is stored in SNS's normalized form:
    /// every retry field present (unset ones null), the other policy sections null and guaranteed
    /// false; a later RawMessageDelivery change keeps the stored policy.
    /// </summary>
    [Test]
    [DisplayName("sns.subscription-delivery-policy-reads-back-normalized-on-an-sqs-subscription")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    [KnownFidelityGap(FidelityGap.SnsSubscriptionAttributes, "A subscription DeliveryPolicy reads back exactly as sent instead of in SNS's normalized form (null retry fields, null sections, guaranteed=false)")]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sns = ctx.Sns;
        var sqs = ctx.Sqs;
        var topic = (await sns.CreateTopicAsync(new CreateTopicRequest { Name = FidelityContext.UniqueName("sim2real-behav-t-") })).TopicArn;
        ctx.OnCleanup(() => sns.DeleteTopicAsync(new DeleteTopicRequest { TopicArn = topic }));
        var url = (await sqs.CreateQueueAsync(new CreateQueueRequest { QueueName = FidelityContext.UniqueName("sim2real-behav-q-") })).QueueUrl;
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(url));
        var queueArn = (await sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest { QueueUrl = url, AttributeNames = ["QueueArn"] })).Attributes!["QueueArn"];
        var sub = (await sns.SubscribeAsync(new SubscribeRequest
        {
            TopicArn = topic,
            Protocol = "sqs",
            Endpoint = queueArn,
            ReturnSubscriptionArn = true,
        })).SubscriptionArn;
        ctx.OnCleanup(() => sns.UnsubscribeAsync(new UnsubscribeRequest { SubscriptionArn = sub }));

        await sns.SetSubscriptionAttributesAsync(new SetSubscriptionAttributesRequest
        {
            SubscriptionArn = sub,
            AttributeName = "DeliveryPolicy",
            AttributeValue = Sent().ToJsonString(),
        });
        var attrs = (await sns.GetSubscriptionAttributesAsync(new GetSubscriptionAttributesRequest { SubscriptionArn = sub })).Attributes ?? [];
        var stored = JsonNode.Parse(attrs.GetValueOrDefault("DeliveryPolicy", "null"));
        Ensure(JsonNode.DeepEquals(stored, Stored()), $"the stored DeliveryPolicy is {Repr(stored)}, expected {Repr(Stored())}");
        Ensure(attrs.GetValueOrDefault("Protocol") == "sqs", $"subscription protocol '{attrs.GetValueOrDefault("Protocol")}'");

        await sns.SetSubscriptionAttributesAsync(new SetSubscriptionAttributesRequest
        {
            SubscriptionArn = sub,
            AttributeName = "RawMessageDelivery",
            AttributeValue = "true",
        });
        attrs = (await sns.GetSubscriptionAttributesAsync(new GetSubscriptionAttributesRequest { SubscriptionArn = sub })).Attributes ?? [];
        Ensure(attrs.GetValueOrDefault("RawMessageDelivery") == "true",
            $"RawMessageDelivery not stored: {{{string.Join(", ", attrs.Select(kv => $"'{kv.Key}': '{kv.Value}'"))}}}");
        Ensure(JsonNode.DeepEquals(JsonNode.Parse(attrs.GetValueOrDefault("DeliveryPolicy", "null")), Stored()),
            $"a RawMessageDelivery change altered the DeliveryPolicy: '{attrs.GetValueOrDefault("DeliveryPolicy")}'");
    }

    private static string Repr(JsonNode? node) => node?.ToJsonString() ?? "None";
}
