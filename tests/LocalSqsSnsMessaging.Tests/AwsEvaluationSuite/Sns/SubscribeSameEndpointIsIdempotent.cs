// Translated from behavioral/sns/check_subscribe_same_endpoint_is_idempotent.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Text.Json.Nodes;
using Amazon.SimpleNotificationService;
using Amazon.SimpleNotificationService.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sns;

/// <summary>sns.subscribe-same-endpoint-is-idempotent</summary>
[Category("AwsEvaluationSuite")]
public sealed class SubscribeSameEndpointIsIdempotent
{
    /// <summary>
    /// A repeat Subscribe on the same (topic, protocol, endpoint) returns the same SubscriptionArn
    /// without a second subscription; identical or omitted Attributes are accepted, different ones are
    /// refused with InvalidParameter and not applied, and a different endpoint gets a new subscription.
    /// </summary>
    [Test]
    [DisplayName("sns.subscribe-same-endpoint-is-idempotent")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sns = ctx.Sns;
        var topicArn = await CreateTopic(sns, ctx, FidelityContext.UniqueName("sim2real-behav-sns-subidem-"));
        var endpoint = FakeQueueArn(topicArn, FidelityContext.UniqueName("sim2real-behav-sns-subidem-q-"));

        var first = await Subscribe(sns, ctx, topicArn, endpoint);
        Ensure(!string.IsNullOrEmpty(first) && first != "pending confirmation",
            $"expected an auto-confirmed sqs SubscriptionArn, got '{first}'");

        var second = (await sns.SubscribeAsync(new SubscribeRequest { TopicArn = topicArn, Protocol = "sqs", Endpoint = endpoint })).SubscriptionArn;
        Ensure(second == first,
            "a repeat Subscribe with the identical (topic, protocol, endpoint) triple must " +
            $"return the same SubscriptionArn; got '{second}' != '{first}'");

        var arns = await Poll(
            () => SubArns(sns, topicArn),
            a => a.Count >= 1,
            timeoutS: 30.0,
            intervalS: 2.0,
            what: "the subscription is listed");
        Ensure(arns.SequenceEqual([first]),
            $"expected exactly one subscription on the topic after two Subscribe calls, got {PyList(arns)}");

        // Attributes are part of the idempotency key, so a repeat with different ones is refused.
        // SNS stores a filter policy with insignificant whitespace stripped, so submit the compact
        // form to make the read-back a byte comparison.
        var filterPolicy = new JsonObject { ["event"] = new JsonArray("created") }.ToJsonString();
        var (code, message) = await Error(() => sns.SubscribeAsync(new SubscribeRequest
        {
            TopicArn = topicArn,
            Protocol = "sqs",
            Endpoint = endpoint,
            Attributes = new() { ["FilterPolicy"] = filterPolicy },
        }));
        Ensure(code == "InvalidParameter",
            "a repeat Subscribe on an existing triple carrying DIFFERENT Attributes must " +
            $"be refused with InvalidParameter, not silently accepted; got '{code}'");
        Ensure(message == "Invalid parameter: Attributes Reason: Subscription already exists with different attributes",
            "the refusal must say the existing subscription differs in its attributes -- " +
            "InvalidParameter alone is what every malformed Subscribe yields, so the " +
            $"message is the only thing that identifies this cause; got '{message}'");
        var arnsAfter = await SubArns(sns, topicArn);
        Ensure(arnsAfter.SequenceEqual([first]),
            $"expected still exactly one subscription after three Subscribe calls, got {PyList(arnsAfter)}");
        var stored = (await sns.GetSubscriptionAttributesAsync(new GetSubscriptionAttributesRequest { SubscriptionArn = first })).Attributes ?? [];
        Ensure(!stored.ContainsKey("FilterPolicy"),
            "the refused Subscribe must not have set a FilterPolicy on the existing " +
            $"subscription, got '{stored.GetValueOrDefault("FilterPolicy")}'");

        // The refusal is keyed on a difference, not on the presence of Attributes.
        var attrEndpoint = FakeQueueArn(topicArn, FidelityContext.UniqueName("sim2real-behav-sns-subidem-q3-"));
        var attrFirst = await Subscribe(sns, ctx, topicArn, attrEndpoint, attributes: new() { ["FilterPolicy"] = filterPolicy });
        var sameAttrs = (await sns.SubscribeAsync(new SubscribeRequest
        {
            TopicArn = topicArn,
            Protocol = "sqs",
            Endpoint = attrEndpoint,
            Attributes = new() { ["FilterPolicy"] = filterPolicy },
        })).SubscriptionArn;
        Ensure(sameAttrs == attrFirst,
            "a repeat Subscribe carrying the IDENTICAL Attributes must be accepted and " +
            $"return '{attrFirst}', got '{sameAttrs}'");
        var omittedAttrs = (await sns.SubscribeAsync(new SubscribeRequest { TopicArn = topicArn, Protocol = "sqs", Endpoint = attrEndpoint })).SubscriptionArn;
        Ensure(omittedAttrs == attrFirst,
            "omitting Attributes on a repeat is not a difference and must be accepted, " +
            $"returning '{attrFirst}', got '{omittedAttrs}'");
        var surviving = (await sns.GetSubscriptionAttributesAsync(new GetSubscriptionAttributesRequest { SubscriptionArn = attrFirst }))
            .Attributes?.GetValueOrDefault("FilterPolicy");
        Ensure(surviving == filterPolicy,
            "the repeat that omitted Attributes must not have cleared or rewritten the " +
            $"FilterPolicy; expected '{filterPolicy}', got '{surviving}'");

        // A different endpoint mints a new subscription, so the identity above is endpoint-keyed.
        var otherEndpoint = FakeQueueArn(topicArn, FidelityContext.UniqueName("sim2real-behav-sns-subidem-q2-"));
        var other = await Subscribe(sns, ctx, topicArn, otherEndpoint);
        Ensure(other != first, $"a distinct endpoint must produce a distinct SubscriptionArn, got '{other}' == '{first}'");
        Ensure(other != attrFirst,
            "a distinct endpoint must produce a distinct SubscriptionArn, got " +
            $"'{other}' == '{attrFirst}'");
        // One subscription per endpoint, however many times each was subscribed.
        var expected = new[] { first, attrFirst, other }.Order(StringComparer.Ordinal).ToList();
        var listed = await Poll(
            () => SubArns(sns, topicArn),
            a => a.Count >= 3,
            timeoutS: 30.0,
            intervalS: 2.0,
            what: "all three distinct subscriptions are listed");
        var listedSorted = listed.Order(StringComparer.Ordinal).ToList();
        Ensure(listedSorted.SequenceEqual(expected),
            $"expected exactly the three distinct subscriptions {PyList(expected)} after seven " +
            $"accepted Subscribe calls on three endpoints, got {PyList(listedSorted)}");
    }

    private static string PyList(IEnumerable<string> values) => $"[{string.Join(", ", values.Select(v => $"'{v}'"))}]";

    /// <summary>CreateTopic + register best-effort DeleteTopic cleanup; returns the TopicArn.</summary>
    private static async Task<string> CreateTopic(AmazonSimpleNotificationServiceClient client, FidelityContext ctx, string name,
        Dictionary<string, string>? attributes = null)
    {
        var request = new CreateTopicRequest { Name = name };
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

    /// <summary>Subscribe an sqs endpoint; register unsubscribe cleanup; return the SubscriptionArn.</summary>
    private static async Task<string> Subscribe(AmazonSimpleNotificationServiceClient client, FidelityContext ctx, string topicArn, string queueArn,
        Dictionary<string, string>? attributes = null)
    {
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
    private static async Task<List<string>> Poll(Func<Task<List<string>>> fn, Func<List<string>, bool> pred, double timeoutS, double intervalS, string what)
    {
        var deadline = FidelityTime.StartNew();
        var val = await fn();
        while (!pred(val))
        {
            if (deadline.Elapsed >= TimeSpan.FromSeconds(timeoutS))
            {
                throw Fail($"{what}: condition not met within {timeoutS}s, last value {PyList(val)}");
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(intervalS));
            val = await fn();
        }

        return val;
    }

    /// <summary>(code, message) for a refused call; an unexpected success raises.</summary>
    private static async Task<(string? Code, string Message)> Error<T>(Func<Task<T>> fn)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is null)
        {
            throw Fail($"real AWS refuses this call, but it SUCCEEDED and returned {result}");
        }

        return (error.Code, error.Message ?? "");
    }

    private static async Task<List<string>> SubArns(AmazonSimpleNotificationServiceClient client, string topicArn) =>
        [.. ((await client.ListSubscriptionsByTopicAsync(new ListSubscriptionsByTopicRequest { TopicArn = topicArn })).Subscriptions ?? []).Select(s => s.SubscriptionArn)];
}
