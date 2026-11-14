// Translated from behavioral/sns/check_filter_policy_operator_vocabulary_round_trip.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Text.Json;
using System.Text.Json.Nodes;
using Amazon.SimpleNotificationService;
using Amazon.SimpleNotificationService.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sns;

/// <summary>sns.filter-policy-operator-vocabulary-round-trip</summary>
[Category("AwsEvaluationSuite")]
public sealed class FilterPolicyOperatorVocabularyRoundTrip
{
    private const string Check = "filter-policy-operator-vocabulary-round-trip";

    /// <summary>
    /// Every filter-policy operator round-trips byte-exact via Subscribe and $or via
    /// SetSubscriptionAttributes, a 6-key policy fails InvalidParameter, and {} clears the policy.
    /// Reference: https://docs.aws.amazon.com/sns/latest/dg/sns-subscription-filter-policies.html
    /// </summary>
    [Test]
    [DisplayName("sns.filter-policy-operator-vocabulary-round-trip")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sns = ctx.Sns;
        var topicArn = await CreateTopic(sns, ctx, FidelityContext.UniqueName("sim2real-behav-sns-fpops-"),
            tags: [new() { Key = "sim2real", Value = Check }, new() { Key = "sim2real:managed", Value = "true" }]);

        // Every documented MessageAttributes-scope operator. Keys are ANDed, values within
        // a key are ORed. Split into groups of at most 5 keys, the per-policy ceiling.
        var groups = new List<JsonObject>
        {
            new()
            {
                ["event"] = new JsonArray("order_placed", "order_cancelled"),           // exact string, OR within key
                ["store"] = new JsonArray(new JsonObject { ["prefix"] = "sea" }),        // prefix
                ["region"] = new JsonArray(new JsonObject { ["suffix"] = "-west-2" }),   // suffix
                ["customer_tier"] = new JsonArray(new JsonObject { ["anything-but"] = "free" }), // anything-but, scalar
                ["channel"] = new JsonArray(new JsonObject { ["anything-but"] = new JsonArray("fax", "pager") }), // anything-but, list
            },
            new()
            {
                ["sku"] = new JsonArray(new JsonObject { ["anything-but"] = new JsonObject { ["prefix"] = "TEST-" } }), // anything-but + prefix
                ["attempt"] = new JsonArray(new JsonObject { ["anything-but"] = new JsonArray(1, 2) }),                  // anything-but, numeric list
                ["price"] = new JsonArray(new JsonObject { ["numeric"] = new JsonArray(">", 0, "<=", 100) }),            // numeric range
                ["quantity"] = new JsonArray(new JsonObject { ["numeric"] = new JsonArray("=", 5) }),                    // numeric equality
                ["coupon"] = new JsonArray(new JsonObject { ["exists"] = false }),                                       // exists: false
            },
            new()
            {
                ["order_id"] = new JsonArray(new JsonObject { ["exists"] = true }),                       // exists: true
                ["status"] = new JsonArray(new JsonObject { ["equals-ignore-case"] = "PENDING" }),         // case-insensitive
            },
        };

        string? firstSubArn = null;
        for (var index = 0; index < groups.Count; index++)
        {
            var policy = groups[index];
            Ensure(policy.Count <= 5, $"group {index} exceeds the measured 5-key ceiling");
            // SNS stores the policy with whitespace stripped but otherwise byte-exact, so the
            // compact form is sent and compared byte for byte.
            var submitted = policy.ToJsonString();
            var subArn = await SubscribeFakeQueue(sns, ctx, topicArn, attributes: new() { ["FilterPolicy"] = submitted });
            Ensure(!string.IsNullOrEmpty(subArn) && subArn != "pending confirmation",
                $"expected an auto-confirmed sqs subscription for group {index}, got '{subArn}'");
            firstSubArn ??= subArn;

            var a = subArn;
            var storedRaw = await Poll(
                async () => (await sns.GetSubscriptionAttributesAsync(new GetSubscriptionAttributesRequest { SubscriptionArn = a }))
                    .Attributes?.GetValueOrDefault("FilterPolicy"),
                raw => !string.IsNullOrEmpty(raw),
                timeoutS: 30.0,
                intervalS: 2.0,
                what: $"Subscribe-time FilterPolicy for group {index} became readable",
                repr: raw => raw is null ? "None" : $"'{raw}'");
            Ensure(storedRaw == submitted,
                "the operator vocabulary supplied at Subscribe time must round-trip " +
                "BYTE-for-byte once whitespace is already absent (measured); group " +
                $"{index} expected '{submitted}', got '{storedRaw}'");
            var stored = JsonNode.Parse(storedRaw!)!.AsObject();
            // Implicit AND across keys: every key survives, none is collapsed or dropped,
            // and key ORDER is preserved (json.loads keeps insertion order).
            Ensure(stored.Select(kv => kv.Key).SequenceEqual(policy.Select(kv => kv.Key)),
                $"expected all {policy.Count} ANDed keys retained in submitted order, got " +
                $"[{string.Join(", ", stored.Select(kv => $"'{kv.Key}'"))}]");
        }

        // Value-array order is part of the document, asserted as a sequence (not a set).
        var group0 = JsonNode.Parse((await sns.GetSubscriptionAttributesAsync(new GetSubscriptionAttributesRequest { SubscriptionArn = firstSubArn }))
            .Attributes!["FilterPolicy"])!.AsObject();
        Ensure(JsonNode.DeepEquals(group0["event"], new JsonArray("order_placed", "order_cancelled")),
            $"expected the value array order preserved as a list, got {PyRepr(group0["event"])}");
        Ensure(JsonNode.DeepEquals(group0["channel"]?[0]?["anything-but"], new JsonArray("fax", "pager")),
            $"expected the anything-but list order preserved, got {PyRepr(group0["channel"])}");

        // The 5-key ceiling itself: a SIXTH top-level key is refused, and the refusal
        // names the limit.
        var sixKeys = groups[0].DeepClone().AsObject();
        sixKeys["overflow"] = new JsonArray("anything");
        var (code, message) = await Error(() => sns.SubscribeAsync(new SubscribeRequest
        {
            TopicArn = topicArn,
            Protocol = "sqs",
            Endpoint = FakeQueueArn(topicArn, FidelityContext.UniqueName("sim2real-behav-sns-fpops-q6-")),
            Attributes = new() { ["FilterPolicy"] = sixKeys.ToJsonString() },
        }));
        Ensure(code == "InvalidParameter", $"a 6-key filter policy must be refused with InvalidParameter; got '{code}'");
        const string expected = "Invalid parameter: Attributes Reason: FilterPolicy: Filter policy can not have more than 5 keys";
        Ensure(message == expected,
            "the refusal must name the 5-key ceiling -- InvalidParameter alone is what " +
            $"every malformed Subscribe attribute yields; expected '{expected}', got '{message}'");

        var sub = firstSubArn!;

        // The SetSubscriptionAttributes path must accept the same vocabulary, including $or,
        // which can only appear at the top level of a policy.
        var orPolicy = new JsonObject
        {
            ["$or"] = new JsonArray(
                new JsonObject { ["metric_name"] = new JsonArray("CPUUtilization") },
                new JsonObject { ["namespace"] = new JsonArray("AWS/EC2"), ["period"] = new JsonArray(new JsonObject { ["numeric"] = new JsonArray(">=", 60) }) }),
            ["severity"] = new JsonArray(new JsonObject { ["anything-but"] = new JsonObject { ["prefix"] = "INFO" } }),
        };
        await sns.SetSubscriptionAttributesAsync(new SetSubscriptionAttributesRequest
        {
            SubscriptionArn = sub, AttributeName = "FilterPolicy", AttributeValue = PyDumps(orPolicy),
        });
        var replaced = await Poll(
            () => FilterPolicy(sns, sub),
            p => p.ContainsKey("$or"),
            timeoutS: 30.0,
            intervalS: 2.0,
            what: "$or FilterPolicy replaced the previous policy",
            repr: PyRepr);
        Ensure(JsonNode.DeepEquals(replaced, orPolicy),
            $"expected the $or policy to replace and round-trip unchanged, got {PyRepr(replaced)}");
        Ensure(replaced["$or"] is JsonArray { Count: 2 } branches && branches[0] is JsonObject b0 && b0.ContainsKey("metric_name"),
            $"expected the $or branch list order preserved as a sequence, got {PyRepr(replaced["$or"])}");
        Ensure(!replaced.ContainsKey("event"),
            "SetSubscriptionAttributes replaces the FilterPolicy wholesale; the previous " +
            $"policy's keys must be gone, got [{string.Join(", ", replaced.Select(kv => $"'{kv.Key}'").Order(StringComparer.Ordinal))}]");

        // An empty policy object clears filtering: the previous document must not linger.
        await sns.SetSubscriptionAttributesAsync(new SetSubscriptionAttributesRequest
        {
            SubscriptionArn = sub, AttributeName = "FilterPolicy", AttributeValue = "{}",
        });
        var cleared = await Poll(
            () => FilterPolicy(sns, sub),
            p => !p.ContainsKey("$or"),
            timeoutS: 30.0,
            intervalS: 2.0,
            what: "FilterPolicy cleared to {}",
            repr: PyRepr);
        Ensure(cleared.Count == 0, $"expected FilterPolicy cleared to an empty policy, got {PyRepr(cleared)}");
    }

    /// <summary>CreateTopic + register best-effort DeleteTopic cleanup; returns the TopicArn.</summary>
    private static async Task<string> CreateTopic(AmazonSimpleNotificationServiceClient client, FidelityContext ctx, string name,
        Dictionary<string, string>? attributes = null, List<Amazon.SimpleNotificationService.Model.Tag>? tags = null)
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
    /// A well-formed SQS queue ARN in the topic's account/region.
    /// SNS confirms an sqs-protocol subscription without verifying the queue exists, so this
    /// keeps the whole check single-service: no SQS resource is ever created.
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

    private static async Task<JsonObject> FilterPolicy(AmazonSimpleNotificationServiceClient client, string subArn)
    {
        var attrs = (await client.GetSubscriptionAttributesAsync(new GetSubscriptionAttributesRequest { SubscriptionArn = subArn })).Attributes ?? [];
        var raw = attrs.GetValueOrDefault("FilterPolicy");
        return JsonNode.Parse(string.IsNullOrEmpty(raw) ? "{}" : raw)!.AsObject();
    }

    /// <summary>Call fn(); return (code, message) of the ClientError it raises; a success raises.</summary>
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

    /// <summary><c>json.dumps(value)</c> with Python's default separators (", " and ": ").</summary>
    private static string PyDumps(JsonNode? node) => node switch
    {
        null => "null",
        JsonObject o => "{" + string.Join(", ", o.Select(kv => $"{JsonSerializer.Serialize(kv.Key)}: {PyDumps(kv.Value)}")) + "}",
        JsonArray a => "[" + string.Join(", ", a.Select(PyDumps)) + "]",
        _ => node.ToJsonString(),
    };

    /// <summary>Python's <c>repr</c> of a parsed JSON value.</summary>
    private static string PyRepr(JsonNode? node) => node switch
    {
        null => "None",
        JsonObject o => "{" + string.Join(", ", o.Select(kv => $"'{kv.Key}': {PyRepr(kv.Value)}")) + "}",
        JsonArray a => "[" + string.Join(", ", a.Select(PyRepr)) + "]",
        JsonValue v when v.GetValueKind() == JsonValueKind.String => $"'{v.GetValue<string>()}'",
        JsonValue v when v.GetValueKind() == JsonValueKind.True => "True",
        JsonValue v when v.GetValueKind() == JsonValueKind.False => "False",
        _ => node.ToJsonString(),
    };
}
