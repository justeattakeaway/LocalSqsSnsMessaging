// Translated from behavioral/sns/check_filter_policy_unknown_operator_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Amazon.SimpleNotificationService;
using Amazon.SimpleNotificationService.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sns;

/// <summary>sns.filter-policy-unknown-operator-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class FilterPolicyUnknownOperatorRejected
{
    private const string Check = "filter-policy-unknown-operator-rejected";

    /// <summary>
    /// SetSubscriptionAttributes refuses a FilterPolicy with an operator SNS lacks, InvalidParameter
    /// with a reason-specific message, leaving the stored policy unchanged; cidr/wildcard are accepted.
    /// </summary>
    [Test]
    [DisplayName("sns.filter-policy-unknown-operator-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sns = ctx.Sns;
        var topicArn = await CreateTopic(
            sns, ctx, FidelityContext.UniqueName("sim2real-behav-sns-fpbadop-"),
            tags: [new() { Key = "sim2real", Value = Check }, new() { Key = "sim2real:managed", Value = "true" }]);

        // Valid setup: a policy that really is accepted, so the negative below differs from
        // the control by exactly one thing -- the operator name.
        var baseline = JsonNode.Parse("""{"price": [{"numeric": [">", 5]}]}""")!;
        var subArn = await SubscribeFakeQueue(
            sns, ctx, topicArn, attributes: new() { ["FilterPolicy"] = PyDumps(baseline) });
        var stored = await Poll(
            () => FilterPolicy(sns, subArn),
            p => !JsonNode.DeepEquals(p, new JsonObject()),
            timeoutS: 30.0,
            intervalS: 2.0,
            what: "baseline FilterPolicy readable");
        Ensure(JsonNode.DeepEquals(stored, baseline), $"expected the baseline policy stored, got {Repr(stored)}");

        // `cidr` and `wildcard` are part of the SNS vocabulary (not EventBridge-only) and are
        // stored verbatim, so they must be accepted.
        (string Name, string Policy)[] implemented =
        [
            ("cidr", """{"source_ip": [{"cidr": "10.0.0.0/24"}]}"""),
            ("wildcard", """{"host": [{"wildcard": "*.example.com"}]}"""),
            ("anything-but + wildcard", """{"host": [{"anything-but": {"wildcard": "*.test.com"}}]}"""),
            ("prefix + equals-ignore-case", """{"host": [{"prefix": {"equals-ignore-case": "ABC"}}]}"""),
        ];
        foreach (var (name, policy) in implemented)
        {
            // json.dumps(policy, separators=(",", ":")).
            var submitted = JsonNode.Parse(policy)!.ToJsonString();
            // The operator is implemented by SNS: a refusal raises here.
            await sns.SetSubscriptionAttributesAsync(new SetSubscriptionAttributesRequest
            {
                SubscriptionArn = subArn,
                AttributeName = "FilterPolicy",
                AttributeValue = submitted,
            });
            var applied = await Poll(
                async () => (await sns.GetSubscriptionAttributesAsync(new GetSubscriptionAttributesRequest { SubscriptionArn = subArn }))
                    .Attributes?.GetValueOrDefault("FilterPolicy"),
                raw => raw == submitted,
                timeoutS: 30.0,
                intervalS: 2.0,
                what: $"the {name} policy became readable");
            Ensure(applied == submitted,
                $"the {name} policy must round-trip byte-for-byte, got '{applied}'");
        }

        // Back to the baseline so each refusal below is provably a no-op against a known
        // document.
        await sns.SetSubscriptionAttributesAsync(new SetSubscriptionAttributesRequest
        {
            SubscriptionArn = subArn,
            AttributeName = "FilterPolicy",
            AttributeValue = PyDumps(baseline),
        });
        await Poll(
            () => FilterPolicy(sns, subArn),
            p => JsonNode.DeepEquals(p, baseline),
            timeoutS: 30.0,
            intervalS: 2.0,
            what: "baseline FilterPolicy restored before the refusal legs");

        // Every leg is InvalidParameter, so the message is the only signal of the reason.
        (string Description, string Policy, string ExpectedText)[] refused =
        [
            (
                "a match type SNS does not implement",
                """{"price": [{"greater-than": 5}]}""",
                "Invalid parameter: FilterPolicy: Unrecognized match type greater-than\n at [Source: (String)\"{\"price\":[{\"greater-than\":5}]}\"; line: 1, column: 28]"
            ),
            (
                "$and, which SNS does not have even though it has $or",
                """{"$and": [{"a": ["1"]}, {"b": ["2"]}]}""",
                "Invalid parameter: FilterPolicy: Unrecognized match type a\n at [Source: (String)\"{\"$and\":[{\"a\":[\"1\"]},{\"b\":[\"2\"]}]}\"; line: 1, column: 16]"
            ),
            (
                "the != numeric range operator",
                """{"price": [{"numeric": ["!=", 5]}]}""",
                "Invalid parameter: FilterPolicy: Unrecognized numeric range operator: !=\n at [Source: (String)\"{\"price\":[{\"numeric\":[\"!=\",5]}]}\"; line: 1, column: 29]"
            ),
            (
                "anything-but wrapping cidr -- cidr alone is accepted, negated it is not",
                """{"ip": [{"anything-but": {"cidr": "10.0.0.0/8"}}]}""",
                "Invalid parameter: FilterPolicy: Unsupported anything-but pattern: cidr\n at [Source: (String)\"{\"ip\":[{\"anything-but\":{\"cidr\":\"10.0.0.0/8\"}}]}\"; line: 1, column: 33]"
            ),
            (
                "a non-boolean exists pattern",
                """{"coupon": [{"exists": "yes"}]}""",
                "Invalid parameter: FilterPolicy: exists match pattern must be either true or false.\n at [Source: (String)\"{\"coupon\":[{\"exists\":\"yes\"}]}\"; line: 1, column: 23]"
            ),
        ];
        foreach (var (description, policy, expectedText) in refused)
        {
            var (code, message) = await Error(() => sns.SetSubscriptionAttributesAsync(new SetSubscriptionAttributesRequest
            {
                SubscriptionArn = subArn,
                AttributeName = "FilterPolicy",
                AttributeValue = policy,
            }));
            Ensure(code == "InvalidParameter",
                $"{description} must be refused with InvalidParameter, not stored; got '{code}'");
            Ensure(message == expectedText,
                $"{description}: the refusal must say '{expectedText}' -- all five legs share " +
                "InvalidParameter, so the message is the only thing that proves the refusal " +
                $"was for this reason; got '{message}'");
            // Effect absent, after EVERY leg: a rejected write leaves the document alone.
            var after = await FilterPolicy(sns, subArn);
            Ensure(JsonNode.DeepEquals(after, baseline),
                $"{description} was refused but changed the stored FilterPolicy; expected " +
                $"{Repr(baseline)}, got {Repr(after)}");
        }

        // Control: the same code path still accepts a legal replacement, so the rejections
        // above were about the operator and not about the subscription being unwritable.
        var control = JsonNode.Parse("""{"price": [{"numeric": [">=", 10, "<", 20]}], "event": ["ship"]}""")!;
        await sns.SetSubscriptionAttributesAsync(new SetSubscriptionAttributesRequest
        {
            SubscriptionArn = subArn,
            AttributeName = "FilterPolicy",
            AttributeValue = PyDumps(control),
        });
        var final = await Poll(
            () => FilterPolicy(sns, subArn),
            p => p is JsonObject o && o.ContainsKey("event"),
            timeoutS: 30.0,
            intervalS: 2.0,
            what: "control FilterPolicy applied");
        Ensure(JsonNode.DeepEquals(final, control), $"expected the control policy to be accepted, got {Repr(final)}");
    }

    private static string Repr(JsonNode? node) => node?.ToJsonString() ?? "None";

    /// <summary>Python's json.dumps with its default separators (", " and ": ").</summary>
    private static string PyDumps(JsonNode? node) => node switch
    {
        JsonObject o => $"{{{string.Join(", ", o.Select(kv => $"{JsonValue.Create(kv.Key).ToJsonString(PyJson)}: {PyDumps(kv.Value)}"))}}}",
        JsonArray a => $"[{string.Join(", ", a.Select(PyDumps))}]",
        null => "null",
        _ => node.ToJsonString(PyJson),
    };

    // json.dumps escapes only quotes, backslashes and control characters in ASCII text.
    private static readonly JsonSerializerOptions PyJson = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

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

    /// <summary>Poll fn() until pred(val); raise AssertionError on deadline miss (never a bare sleep).</summary>
    private static async Task<T> Poll<T>(Func<Task<T>> fn, Func<T, bool> pred, double timeoutS, double intervalS, string what)
    {
        var deadline = FidelityTime.StartNew();
        var val = await fn();
        while (!pred(val))
        {
            if (deadline.Elapsed >= TimeSpan.FromSeconds(timeoutS))
            {
                throw Fail($"{what}: condition not met within {timeoutS}s, last value {(val is JsonNode n ? Repr(n) : $"'{val}'")}");
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(intervalS));
            val = await fn();
        }

        return val;
    }

    private static async Task<JsonNode?> FilterPolicy(AmazonSimpleNotificationServiceClient client, string subArn)
    {
        var attrs = (await client.GetSubscriptionAttributesAsync(new GetSubscriptionAttributesRequest { SubscriptionArn = subArn })).Attributes ?? [];
        var raw = attrs.GetValueOrDefault("FilterPolicy");
        return JsonNode.Parse(string.IsNullOrEmpty(raw) ? "{}" : raw);
    }

    /// <summary>
    /// Call fn(); return (code, message) of the ClientError it raises. An unexpected success
    /// raises, so a target that accepts what AWS refuses never reads as a code mismatch.
    /// </summary>
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
