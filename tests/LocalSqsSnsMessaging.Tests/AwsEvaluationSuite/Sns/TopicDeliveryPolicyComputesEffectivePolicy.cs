// Translated from behavioral/sns/check_topic_delivery_policy_computes_effective_policy.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Text.Json;
using System.Text.Json.Nodes;
using Amazon.SimpleNotificationService;
using Amazon.SimpleNotificationService.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sns;

/// <summary>sns.topic-delivery-policy-computes-effective-policy</summary>
[Category("AwsEvaluationSuite")]
public sealed class TopicDeliveryPolicyComputesEffectivePolicy
{
    private const string Check = "topic-delivery-policy-computes-effective-policy";

    /// <summary>
    /// A topic DeliveryPolicy is validated synchronously and merged over the documented defaults into
    /// a computed EffectiveDeliveryPolicy; rejected writes leave both attributes unchanged.
    /// Reference: https://docs.aws.amazon.com/sns/latest/dg/sns-message-delivery-retries.html
    /// </summary>
    [Test]
    [DisplayName("sns.topic-delivery-policy-computes-effective-policy")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sns = ctx.Sns;
        var topicArn = await CreateTopic(sns, ctx, FidelityContext.UniqueName("sim2real-behav-sns-delpol-"));

        // Baseline: EffectiveDeliveryPolicy exists before any DeliveryPolicy is set, because it
        // is computed from defaults rather than stored.
        var baseAttrs = await TopicAttrs(sns, topicArn);
        Ensure(baseAttrs.ContainsKey("EffectiveDeliveryPolicy"),
            "EffectiveDeliveryPolicy is computed from account defaults and must be present on a " +
            $"topic with no DeliveryPolicy set; got [{string.Join(", ", baseAttrs.Keys.Order(StringComparer.Ordinal).Select(k => $"'{k}'"))}]");
        var baselineEffective = JsonNode.Parse(baseAttrs["EffectiveDeliveryPolicy"])!.AsObject();
        Ensure(baselineEffective.ContainsKey("http"),
            $"expected the default EffectiveDeliveryPolicy to carry an 'http' section, got {baselineEffective.ToJsonString()}");
        var baselineRetry = baselineEffective["http"]?["defaultHealthyRetryPolicy"] ?? new JsonObject();
        // SNS's documented, account-independent default HTTP retry policy.
        Ensure(NumEq(baselineRetry["numRetries"], 3), $"expected the default numRetries to be 3, got {baselineRetry.ToJsonString()}");
        Ensure(NumEq(baselineRetry["minDelayTarget"], 20), $"expected the default minDelayTarget to be 20, got {baselineRetry.ToJsonString()}");
        Ensure(NumEq(baselineRetry["maxDelayTarget"], 20), $"expected the default maxDelayTarget to be 20, got {baselineRetry.ToJsonString()}");
        Ensure((string?)baselineRetry["backoffFunction"] == "linear", $"expected the default backoffFunction to be 'linear', got {baselineRetry.ToJsonString()}");
        var baselineRetries = baselineRetry["numRetries"]?.ToJsonString() ?? "None";

        // Set a real DeliveryPolicy and require the computed view to follow it.
        var policy = new JsonObject
        {
            ["http"] = new JsonObject
            {
                ["defaultHealthyRetryPolicy"] = new JsonObject
                {
                    ["minDelayTarget"] = 5,
                    ["maxDelayTarget"] = 60,
                    ["numRetries"] = 7,
                    ["numMaxDelayRetries"] = 2,
                    ["numNoDelayRetries"] = 0,
                    ["numMinDelayRetries"] = 1,
                    ["backoffFunction"] = "exponential",
                },
                ["disableSubscriptionOverrides"] = false,
            },
        };
        await sns.SetTopicAttributesAsync(new SetTopicAttributesRequest
        {
            TopicArn = topicArn, AttributeName = "DeliveryPolicy", AttributeValue = policy.ToJsonString(),
        });
        var attrs = await Poll(
            () => TopicAttrs(sns, topicArn),
            a => !string.IsNullOrEmpty(a.GetValueOrDefault("DeliveryPolicy")),
            timeoutS: 30.0,
            intervalS: 2.0,
            what: "DeliveryPolicy becomes readable after SetTopicAttributes");
        var stored = JsonNode.Parse(attrs["DeliveryPolicy"])!;
        Ensure(NumEq(Retry(stored)["numRetries"], 7), $"expected the stored DeliveryPolicy to round-trip numRetries=7, got {stored.ToJsonString()}");
        Ensure((string?)Retry(stored)["backoffFunction"] == "exponential", $"expected backoffFunction to round-trip, got {stored.ToJsonString()}");

        var effective = JsonNode.Parse(attrs["EffectiveDeliveryPolicy"])!;
        Ensure(NumEq(Retry(effective)["numRetries"], 7),
            "EffectiveDeliveryPolicy must be recomputed from the new DeliveryPolicy, not left " +
            $"at the account default of {baselineRetries}; got {effective.ToJsonString()}");
        Ensure(NumEq(Retry(effective)["minDelayTarget"], 5),
            $"expected the overridden minDelayTarget to appear in the effective policy, got {effective.ToJsonString()}");
        // The effective policy is a MERGE, not a copy: keys the override omits must still be
        // populated from the defaults. `disableSubscriptionOverrides` was supplied, so assert on
        // a section the override never mentioned.
        var effectiveHttp = effective["http"]!.AsObject();
        Ensure(effectiveHttp.ContainsKey("defaultRequestPolicy"),
            "EffectiveDeliveryPolicy must be the override merged over the defaults, so " +
            $"http.defaultRequestPolicy must survive; got [{string.Join(", ", effectiveHttp.Select(kv => kv.Key).Order(StringComparer.Ordinal).Select(k => $"'{k}'"))}]");

        // Break exactly one thing: numRetries above the documented maximum.
        var bad = policy.DeepClone();
        Retry(bad)["numRetries"] = 200;
        var (code, message) = await Err(() => sns.SetTopicAttributesAsync(new SetTopicAttributesRequest
        {
            TopicArn = topicArn, AttributeName = "DeliveryPolicy", AttributeValue = bad.ToJsonString(),
        }));
        Ensure(code == "InvalidParameter", $"expected InvalidParameter for numRetries=200 (above the documented max), got {code}");
        Ensure(message == "Invalid parameter: DeliveryPolicy: http.defaultHealthyRetryPolicy.numRetries must be less than or equal to 100",
            $"unexpected refusal message for numRetries=200: '{message}'");

        // A second, structurally different rejection: an unknown backoffFunction.
        var bad2 = policy.DeepClone();
        Retry(bad2)["backoffFunction"] = "quadratic";
        var (code2, _) = await Err(() => sns.SetTopicAttributesAsync(new SetTopicAttributesRequest
        {
            TopicArn = topicArn, AttributeName = "DeliveryPolicy", AttributeValue = bad2.ToJsonString(),
        }));
        Ensure(code2 == "InvalidParameter", $"expected InvalidParameter for an unknown backoffFunction 'quadratic', got {code2}");
        // message not asserted: AWS's text for this refusal embeds server-side parser class
        // names and a byte offset into the submitted document, so it has no portable form.

        // Effect absent: both rejected writes left the accepted policy and its computed view
        // exactly as they were.
        var after = await TopicAttrs(sns, topicArn);
        Ensure(NumEq(Retry(JsonNode.Parse(after["DeliveryPolicy"])!)["numRetries"], 7),
            $"a rejected DeliveryPolicy must not be persisted, got '{after.GetValueOrDefault("DeliveryPolicy")}'");
        Ensure(NumEq(Retry(JsonNode.Parse(after["EffectiveDeliveryPolicy"])!)["numRetries"], 7),
            "a rejected DeliveryPolicy must not perturb the computed EffectiveDeliveryPolicy, " +
            $"got '{after.GetValueOrDefault("EffectiveDeliveryPolicy")}'");

        // Control: a different legal value is still accepted on the same topic, and the
        // computed view follows again.
        Retry(policy)["numRetries"] = 3;
        await sns.SetTopicAttributesAsync(new SetTopicAttributesRequest
        {
            TopicArn = topicArn, AttributeName = "DeliveryPolicy", AttributeValue = policy.ToJsonString(),
        });
        var final = await Poll(
            async () => JsonNode.Parse((await TopicAttrs(sns, topicArn))["EffectiveDeliveryPolicy"])!,
            p => NumEq(Retry(p)["numRetries"], 3),
            timeoutS: 30.0,
            intervalS: 2.0,
            what: "EffectiveDeliveryPolicy follows the control update to numRetries=3");
        Ensure(NumEq(Retry(final)["numRetries"], 3), $"expected the control DeliveryPolicy update to take effect, got {final.ToJsonString()}");
    }

    /// <summary><c>policy["http"]["defaultHealthyRetryPolicy"]</c>; a missing key raises, as the Python lookup does.</summary>
    private static JsonNode Retry(JsonNode policy) =>
        policy["http"]?["defaultHealthyRetryPolicy"] ?? throw new KeyNotFoundException("'defaultHealthyRetryPolicy'");

    /// <summary>Python <c>value == n</c> for a JSON number.</summary>
    private static bool NumEq(JsonNode? value, double n) =>
        value is JsonValue v && v.GetValueKind() == JsonValueKind.Number && v.GetValue<double>() == n;

    /// <summary>CreateTopic (tagged) and register DeleteTopic cleanup; returns the TopicArn.</summary>
    private static async Task<string> CreateTopic(AmazonSimpleNotificationServiceClient client, FidelityContext ctx, string name)
    {
        var arn = (await client.CreateTopicAsync(new CreateTopicRequest
        {
            Name = name,
            Tags = [new() { Key = "sim2real", Value = Check }, new() { Key = "sim2real:managed", Value = "true" }],
        })).TopicArn;
        ctx.OnCleanup(() => client.DeleteTopicAsync(new DeleteTopicRequest { TopicArn = arn }));
        return arn;
    }

    /// <summary>Poll fn() until pred(val); raise AssertionError on deadline miss.</summary>
    private static async Task<T> Poll<T>(Func<Task<T>> fn, Func<T, bool> pred, double timeoutS, double intervalS, string what)
    {
        var deadline = FidelityTime.StartNew();
        var val = await fn();
        while (!pred(val))
        {
            if (deadline.Elapsed >= TimeSpan.FromSeconds(timeoutS))
            {
                throw Fail($"{what}: condition not met within {timeoutS}s, last value {(val is JsonNode j ? j.ToJsonString() : val?.ToString())}");
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(intervalS));
            val = await fn();
        }

        return val;
    }

    private static async Task<Dictionary<string, string>> TopicAttrs(AmazonSimpleNotificationServiceClient client, string topicArn) =>
        (await client.GetTopicAttributesAsync(new GetTopicAttributesRequest { TopicArn = topicArn })).Attributes ?? [];

    /// <summary>Call fn(); return the error (Code, Message). An unexpected SUCCESS raises.</summary>
    private static async Task<(string? Code, string? Message)> Err<T>(Func<Task<T>> fn)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is null)
        {
            throw Fail($"expected the call to be refused, but it SUCCEEDED: {result}");
        }

        return (error.Code, error.Message);
    }
}
