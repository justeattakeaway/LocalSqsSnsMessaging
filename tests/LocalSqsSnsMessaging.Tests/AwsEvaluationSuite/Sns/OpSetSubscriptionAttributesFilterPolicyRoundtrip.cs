// Translated from behavioral/sns/check_op_set_subscription_attributes_filter_policy_roundtrip.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Text.Json.Nodes;
using Amazon.SimpleNotificationService.Model;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sns;

/// <summary>sns.op-set-subscription-attributes-filter-policy-roundtrip</summary>
[Category("AwsEvaluationSuite")]
public sealed class OpSetSubscriptionAttributesFilterPolicyRoundtrip
{
    private const string Check = "op-set-subscription-attributes-filter-policy-roundtrip";

    /// <summary>SetSubscriptionAttributes persists FilterPolicy and FilterPolicyScope readable via Get.</summary>
    [Test]
    [DisplayName("sns.op-set-subscription-attributes-filter-policy-roundtrip")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    [KnownFidelityGap(FidelityGap.SnsSubscriptionAttributes, "A fresh subscription reports FilterPolicy='' instead of omitting the attribute")]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sns = ctx.Sns;
        var arn = (await sns.CreateTopicAsync(new CreateTopicRequest
        {
            Name = FidelityContext.UniqueName("sim2real-behav-topic-subattr-"),
            Tags = [new() { Key = "sim2real", Value = Check }, new() { Key = "sim2real:managed", Value = "true" }],
        })).TopicArn;
        ctx.OnCleanup(() => sns.DeleteTopicAsync(new DeleteTopicRequest { TopicArn = arn }));
        var (_, queueArn) = await CreateSqsQueueForSns(ctx, "subattr-q-");
        var subArn = (await sns.SubscribeAsync(new SubscribeRequest { TopicArn = arn, Protocol = "sqs", Endpoint = queueArn })).SubscriptionArn;
        ctx.OnCleanup(() => sns.UnsubscribeAsync(new UnsubscribeRequest { SubscriptionArn = subArn }));

        var attrs = (await sns.GetSubscriptionAttributesAsync(new GetSubscriptionAttributesRequest { SubscriptionArn = subArn })).Attributes ?? [];
        Ensure(!attrs.ContainsKey("FilterPolicy"), $"a fresh subscription must have no FilterPolicy, got {Repr(attrs)}");

        var policy = new JsonObject { ["tier"] = new JsonArray("gold", "silver") };
        await sns.SetSubscriptionAttributesAsync(new SetSubscriptionAttributesRequest
        {
            SubscriptionArn = subArn, AttributeName = "FilterPolicy", AttributeValue = policy.ToJsonString(),
        });
        await sns.SetSubscriptionAttributesAsync(new SetSubscriptionAttributesRequest
        {
            SubscriptionArn = subArn,
            AttributeName = "FilterPolicyScope",
            AttributeValue = "MessageAttributes",
        });

        async Task<Dictionary<string, string>> Probe() =>
            (await sns.GetSubscriptionAttributesAsync(new GetSubscriptionAttributesRequest { SubscriptionArn = subArn })).Attributes ?? [];

        attrs = await PollUntil(Probe, a => a.ContainsKey("FilterPolicy"), timeoutS: 30, intervalS: 2);
        Ensure(JsonNode.DeepEquals(JsonNode.Parse(attrs["FilterPolicy"]), policy), attrs["FilterPolicy"]);
        Ensure(attrs.GetValueOrDefault("FilterPolicyScope") == "MessageAttributes", Repr(attrs));

        // Clearing with an empty string removes the policy.
        await sns.SetSubscriptionAttributesAsync(new SetSubscriptionAttributesRequest
        {
            SubscriptionArn = subArn, AttributeName = "FilterPolicy", AttributeValue = "",
        });
        attrs = await PollUntil(Probe, a => !a.ContainsKey("FilterPolicy"), timeoutS: 30, intervalS: 2);
        Ensure(!attrs.ContainsKey("FilterPolicy"), Repr(attrs));
    }

    private static string Repr(Dictionary<string, string> d) => "{" + string.Join(", ", d.Select(kv => $"'{kv.Key}': '{kv.Value}'")) + "}";

    /// <summary>Poll probe() until predicate(result) is truthy; return the final result.</summary>
    private static async Task<T> PollUntil<T>(Func<Task<T>> probe, Func<T, bool> predicate, double timeoutS = 60, double intervalS = 1.0, string description = "condition")
    {
        var deadline = FidelityTime.StartNew();
        T? last = default;
        while (deadline.Elapsed < TimeSpan.FromSeconds(timeoutS))
        {
            last = await probe();
            if (predicate(last))
            {
                return last;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(intervalS));
        }

        throw Fail($"{description} not met within {timeoutS}s. Last result: {(last is Dictionary<string, string> d ? Repr(d) : last?.ToString())}");
    }

    /// <summary>
    /// Create an SQS queue with a policy allowing SNS to deliver to it.
    /// Returns (queue_url, queue_arn).
    /// </summary>
    private static async Task<(string Url, string Arn)> CreateSqsQueueForSns(FidelityContext ctx, string namePrefix = "sqs-target-", bool fifo = false)
    {
        var sqs = ctx.Sqs;
        var name = FidelityContext.UniqueName("sim2real-behav-" + namePrefix);
        var attrs = new Dictionary<string, string>();
        if (fifo)
        {
            if (!name.EndsWith(".fifo", StringComparison.Ordinal))
            {
                name += ".fifo";
            }

            attrs["FifoQueue"] = "true";
        }

        var queueUrl = (await sqs.CreateQueueAsync(new CreateQueueRequest
        {
            QueueName = name, Attributes = attrs,
            Tags = new() { ["sim2real"] = Check, ["sim2real:managed"] = "true" },
        })).QueueUrl;
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(queueUrl));
        var arn = (await sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest { QueueUrl = queueUrl, AttributeNames = ["QueueArn"] })).Attributes!["QueueArn"];
        var policy = new JsonObject
        {
            ["Version"] = "2012-10-17",
            ["Statement"] = new JsonArray(new JsonObject
            {
                ["Sid"] = "AllowSNS",
                ["Effect"] = "Allow",
                ["Principal"] = new JsonObject { ["Service"] = "sns.amazonaws.com" },
                ["Action"] = "sqs:SendMessage",
                ["Resource"] = arn,
            }),
        };
        await sqs.SetQueueAttributesAsync(new SetQueueAttributesRequest { QueueUrl = queueUrl, Attributes = new() { ["Policy"] = policy.ToJsonString() } });
        return (queueUrl, arn);
    }
}
