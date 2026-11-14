// Translated from behavioral/sns/check_op_topic_attribute_defaults_and_effective_delivery_policy.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Text.Json.Nodes;
using Amazon.SimpleNotificationService.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sns;

/// <summary>sns.op-topic-attribute-defaults-and-effective-delivery-policy</summary>
[Category("AwsEvaluationSuite")]
public sealed class OpTopicAttributeDefaultsAndEffectiveDeliveryPolicy
{
    /// <summary>A new topic exposes the documented default attribute set and an EffectiveDeliveryPolicy.</summary>
    [Test]
    [DisplayName("sns.op-topic-attribute-defaults-and-effective-delivery-policy")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sns = ctx.Sns;
        var accountId = ctx.Account;

        var arn = (await sns.CreateTopicAsync(new CreateTopicRequest
        {
            Name = FidelityContext.UniqueName("sim2real-behav-topic-defaults-"),
            Tags = [new() { Key = "env", Value = "fidelity" }],
        })).TopicArn;
        ctx.OnCleanup(() => sns.DeleteTopicAsync(new DeleteTopicRequest { TopicArn = arn }));

        var attrs = (await sns.GetTopicAttributesAsync(new GetTopicAttributesRequest { TopicArn = arn })).Attributes ?? [];
        Ensure(attrs["TopicArn"] == arn, Repr(attrs));
        Ensure(attrs["Owner"] == accountId, Repr(attrs));
        Ensure(attrs["SubscriptionsConfirmed"] == "0", Repr(attrs));
        Ensure(attrs["SubscriptionsPending"] == "0", Repr(attrs));
        Ensure(attrs["SubscriptionsDeleted"] == "0", Repr(attrs));
        Ensure(!string.IsNullOrEmpty(attrs["Policy"]), Repr(attrs));
        var effective = JsonNode.Parse(attrs["EffectiveDeliveryPolicy"])!.AsObject();
        Ensure(effective.ContainsKey("http"), effective.ToJsonString());
        Ensure(effective["http"]!.AsObject().ContainsKey("defaultHealthyRetryPolicy"), effective.ToJsonString());
        var retry = effective["http"]!["defaultHealthyRetryPolicy"]!.AsObject();
        Ensure((int)retry["numRetries"]! == 3, retry.ToJsonString());
        Ensure((int)retry["minDelayTarget"]! == 20, retry.ToJsonString());
        Ensure((int)retry["maxDelayTarget"]! == 20, retry.ToJsonString());
        Ensure((string?)retry["backoffFunction"] == "linear", retry.ToJsonString());

        // A standard topic must not advertise FIFO-only attributes.
        Ensure(!attrs.ContainsKey("FifoTopic"), $"a standard topic must not report FifoTopic: {Repr(attrs)}");
        Ensure(!attrs.ContainsKey("ContentBasedDeduplication"), Repr(attrs));

        // The default resource policy is the SNS __default_policy_ID, scoped to the owner.
        var policy = JsonNode.Parse(attrs["Policy"])!.AsObject();
        Ensure((string?)policy["Id"] == "__default_policy_ID", policy.ToJsonString());
        var stmt = policy["Statement"]![0]!.AsObject();
        Ensure((string?)stmt["Sid"] == "__default_statement_ID", stmt.ToJsonString());
        Ensure((string?)stmt["Condition"]!["StringEquals"]!["AWS:SourceOwner"] == accountId, stmt.ToJsonString());

        // By contrast, a FIFO topic DOES advertise the FIFO attributes.
        var fifoArn = (await sns.CreateTopicAsync(new CreateTopicRequest
        {
            Name = FidelityContext.UniqueName("sim2real-behav-topic-defaults-fifo-") + ".fifo",
            Attributes = new() { ["FifoTopic"] = "true" },
            Tags = [new() { Key = "env", Value = "fidelity" }],
        })).TopicArn;
        ctx.OnCleanup(() => sns.DeleteTopicAsync(new DeleteTopicRequest { TopicArn = fifoArn }));
        var fifoAttrs = (await sns.GetTopicAttributesAsync(new GetTopicAttributesRequest { TopicArn = fifoArn })).Attributes ?? [];
        Ensure(fifoAttrs["FifoTopic"] == "true", Repr(fifoAttrs));
        Ensure(fifoAttrs["ContentBasedDeduplication"] == "false", Repr(fifoAttrs));
    }

    private static string Repr(Dictionary<string, string> d) =>
        "{" + string.Join(", ", d.Select(kv => $"'{kv.Key}': '{kv.Value}'")) + "}";
}
