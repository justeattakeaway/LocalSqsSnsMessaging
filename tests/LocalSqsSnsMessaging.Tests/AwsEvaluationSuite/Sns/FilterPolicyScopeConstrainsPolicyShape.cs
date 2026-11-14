// Translated from behavioral/sns/check_filter_policy_scope_constrains_policy_shape.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Text.Json.Nodes;
using Amazon.SimpleNotificationService;
using Amazon.SimpleNotificationService.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sns;

/// <summary>sns.filter-policy-scope-constrains-policy-shape</summary>
[Category("AwsEvaluationSuite")]
public sealed class FilterPolicyScopeConstrainsPolicyShape
{
    /// <summary>
    /// FilterPolicyScope accepts only MessageAttributes/MessageBody, and a nested FilterPolicy is
    /// refused under MessageAttributes scope but accepted once the scope is MessageBody.
    /// </summary>
    [Test]
    [DisplayName("sns.filter-policy-scope-constrains-policy-shape")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    [KnownFidelityGap(FidelityGap.SnsFilterPolicy, "a nested policy under MessageAttributes scope is refused with 'Invalid parameter: FilterPolicy: Filter policy scope ...' instead of 'Invalid parameter: Filter policy scope ...'")]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sns = ctx.Sns;
        var topicArn = await CreateTopic(sns, ctx, FidelityContext.UniqueName("sim2real-behav-sns-fpscope-"));

        // Valid setup: a flat attribute-scope policy, the SNS default.
        var baseline = new JsonObject { ["event"] = new JsonArray("created") };
        var subArn = await SubscribeFakeQueue(sns, ctx, topicArn, attributes: new() { ["FilterPolicy"] = baseline.ToJsonString() });
        var attrs = await Poll(
            () => SubAttrs(sns, subArn),
            a => !string.IsNullOrEmpty(a.GetValueOrDefault("FilterPolicy")),
            timeoutS: 30.0,
            intervalS: 2.0,
            what: "baseline FilterPolicy readable",
            repr: Repr);
        Ensure(JsonNode.DeepEquals(JsonNode.Parse(attrs["FilterPolicy"]), baseline),
            $"expected the baseline policy stored, got '{attrs.GetValueOrDefault("FilterPolicy")}'");
        Ensure(attrs.GetValueOrDefault("FilterPolicyScope", "MessageAttributes") == "MessageAttributes",
            $"expected the default FilterPolicyScope=MessageAttributes, got '{attrs.GetValueOrDefault("FilterPolicyScope")}'");

        // 1. Break exactly one thing: an out-of-vocabulary scope value. The Smithy model has no
        // enum trait on FilterPolicyScope, so this closed vocabulary is server-side only.
        var (codeScope, msgScope) = await Error(() => sns.SetSubscriptionAttributesAsync(new SetSubscriptionAttributesRequest
        {
            SubscriptionArn = subArn, AttributeName = "FilterPolicyScope", AttributeValue = "MessageHeaders",
        }));
        Ensure(codeScope == "InvalidParameter", $"expected InvalidParameter for FilterPolicyScope=MessageHeaders, got {codeScope}");
        var expected = "Invalid parameter: FilterPolicyScope: Invalid value [MessageHeaders]. Please use either MessageBody or MessageAttributes";
        Ensure(msgScope == expected, $"expected message '{expected}', got '{msgScope}'");
        // Effect absent: the scope is still the default.
        var scopeAfter = (await SubAttrs(sns, subArn)).GetValueOrDefault("FilterPolicyScope", "MessageAttributes");
        Ensure(scopeAfter == "MessageAttributes", $"a rejected FilterPolicyScope must not be persisted, got '{scopeAfter}'");

        // 2. Break exactly one thing: a nested policy while the scope is MessageAttributes
        // (attributes are a flat name->value map, so a nested policy cannot be evaluated).
        var nested = new JsonObject { ["store"] = new JsonObject { ["city"] = new JsonArray("seattle") } };
        var (codeNested, msgNested) = await Error(() => sns.SetSubscriptionAttributesAsync(new SetSubscriptionAttributesRequest
        {
            SubscriptionArn = subArn,
            AttributeName = "FilterPolicy",
            AttributeValue = nested.ToJsonString(),
        }));
        Ensure(codeNested == "InvalidParameter",
            "expected InvalidParameter for a nested policy under MessageAttributes scope " +
            $"(attributes are a flat map and cannot be descended), got {codeNested}");
        expected = "Invalid parameter: Filter policy scope MessageAttributes does not support nested filter policy";
        Ensure(msgNested == expected, $"expected message '{expected}', got '{msgNested}'");
        // Effect absent: the flat baseline policy survived the rejected nested write.
        var policyAfter = ParsePolicy((await SubAttrs(sns, subArn)).GetValueOrDefault("FilterPolicy"));
        Ensure(JsonNode.DeepEquals(policyAfter, baseline),
            $"a rejected nested FilterPolicy must not be persisted; expected {baseline.ToJsonString()}, got {policyAfter.ToJsonString()}");

        // Control: the SAME nested policy becomes legal once the scope really is MessageBody,
        // proving the rejection above was the scope/shape coupling and not the policy itself.
        await sns.SetSubscriptionAttributesAsync(new SetSubscriptionAttributesRequest
        {
            SubscriptionArn = subArn, AttributeName = "FilterPolicyScope", AttributeValue = "MessageBody",
        });
        await Poll(
            async () => (await SubAttrs(sns, subArn)).GetValueOrDefault("FilterPolicyScope"),
            v => v == "MessageBody",
            timeoutS: 30.0,
            intervalS: 2.0,
            what: "FilterPolicyScope switched to MessageBody",
            repr: v => $"'{v}'");
        await sns.SetSubscriptionAttributesAsync(new SetSubscriptionAttributesRequest
        {
            SubscriptionArn = subArn,
            AttributeName = "FilterPolicy",
            AttributeValue = nested.ToJsonString(),
        });
        var final = await Poll(
            async () => ParsePolicy((await SubAttrs(sns, subArn)).GetValueOrDefault("FilterPolicy")),
            p => p is JsonObject o && o.ContainsKey("store"),
            timeoutS: 30.0,
            intervalS: 2.0,
            what: "nested FilterPolicy accepted under MessageBody scope",
            repr: p => p.ToJsonString());
        Ensure(JsonNode.DeepEquals(final, nested), $"expected the nested policy accepted under MessageBody scope, got {final.ToJsonString()}");
    }

    /// <summary><c>json.loads(value or "{}")</c>.</summary>
    private static JsonNode ParsePolicy(string? value) => JsonNode.Parse(string.IsNullOrEmpty(value) ? "{}" : value)!;

    private static string Repr(Dictionary<string, string> d) =>
        "{" + string.Join(", ", d.Select(kv => $"'{kv.Key}': '{kv.Value}'")) + "}";

    /// <summary>CreateTopic + register best-effort DeleteTopic cleanup; returns the TopicArn.</summary>
    private static async Task<string> CreateTopic(AmazonSimpleNotificationServiceClient client, FidelityContext ctx, string name,
        Dictionary<string, string>? attributes = null)
    {
        var request = new CreateTopicRequest { Name = name, Tags = [new() { Key = "sim2real", Value = "true" }] };
        if (attributes is { Count: > 0 })
        {
            request.Attributes = attributes;
        }

        var arn = (await client.CreateTopicAsync(request)).TopicArn;
        ctx.OnCleanup(() => client.DeleteTopicAsync(new DeleteTopicRequest { TopicArn = arn }));
        return arn;
    }

    /// <summary>
    /// A well-formed SQS queue ARN in the topic's account/region (SNS confirms sqs
    /// subscriptions without verifying the queue exists, keeping this single-service).
    /// </summary>
    private static string FakeQueueArn(string topicArn, string? name = null)
    {
        var parts = topicArn.Split(':');
        name ??= $"sim2real-behav-sns-q-{Guid.NewGuid().ToString("N")[..8]}";
        return $"arn:aws:sqs:{parts[3]}:{parts[4]}:{name}";
    }

    /// <summary>Subscribe an sqs-protocol endpoint; register unsubscribe cleanup; return the SubscriptionArn.</summary>
    private static async Task<string> SubscribeFakeQueue(AmazonSimpleNotificationServiceClient client, FidelityContext ctx, string topicArn,
        string? queueArn = null, Dictionary<string, string>? attributes = null)
    {
        queueArn ??= FakeQueueArn(topicArn);
        var request = new SubscribeRequest { TopicArn = topicArn, Protocol = "sqs", Endpoint = queueArn };
        if (attributes is { Count: > 0 })
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

    /// <summary>Poll fn() until pred(val); raise AssertionError on deadline miss.</summary>
    private static async Task<T> Poll<T>(Func<Task<T>> fn, Func<T, bool> pred, double timeoutS, double intervalS, string what, Func<T, string> repr)
    {
        var deadline = FidelityTime.StartNew();
        var val = await fn();
        while (!pred(val))
        {
            if (deadline.Elapsed >= TimeSpan.FromSeconds(timeoutS))
            {
                throw Fail($"{what}: condition not met within {timeoutS}s, last value {repr(val)}");
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(intervalS));
            val = await fn();
        }

        return val;
    }

    private static async Task<Dictionary<string, string>> SubAttrs(AmazonSimpleNotificationServiceClient client, string subArn)
        => (await client.GetSubscriptionAttributesAsync(new GetSubscriptionAttributesRequest { SubscriptionArn = subArn })).Attributes ?? [];

    /// <summary>Call fn() and return (code, message) of the ClientError it raises; a success raises.</summary>
    private static async Task<(string? Code, string? Message)> Error<T>(Func<Task<T>> fn)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is null)
        {
            throw Fail($"expected the call to be refused, but it succeeded and returned {result}");
        }

        return (error.Code, error.Message);
    }
}
