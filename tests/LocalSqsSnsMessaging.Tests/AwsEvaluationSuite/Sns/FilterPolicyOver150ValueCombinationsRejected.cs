// Translated from behavioral/sns/check_filter_policy_over_150_value_combinations_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Text.Json;
using System.Text.Json.Nodes;
using Amazon.SimpleNotificationService;
using Amazon.SimpleNotificationService.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sns;

/// <summary>sns.filter-policy-over-150-value-combinations-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class FilterPolicyOver150ValueCombinationsRejected
{
    private const string Check = "filter-policy-over-150-value-combinations-rejected";

    /// <summary>
    /// A filter policy of 5x5x6 = 150 value combinations is accepted by SetSubscriptionAttributes
    /// and Subscribe; 5x5x7 = 175 is refused on both with InvalidParameter "too complex", leaving
    /// the previous policy in place and creating no subscription.
    /// </summary>
    [Test]
    [DisplayName("sns.filter-policy-over-150-value-combinations-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    [KnownFidelityGap(FidelityGap.SnsSubscriptions, "Subscribe to an sqs endpoint whose queue does not exist is refused with NotFound 'Queue not found.'; AWS confirms it without resolving the queue")]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sns = ctx.Sns;
        var topic = (await sns.CreateTopicAsync(new CreateTopicRequest
        {
            Name = FidelityContext.UniqueName("sim2real-behav-sns-fpcx-"),
            Tags = [new() { Key = "sim2real", Value = Check }],
        })).TopicArn;
        ctx.OnCleanup(() => sns.DeleteTopicAsync(new DeleteTopicRequest { TopicArn = topic }));
        var (region, account) = (topic.Split(':')[3], topic.Split(':')[4]);

        // SNS confirms an sqs subscription without resolving the queue.
        string QueueArn() => $"arn:aws:sqs:{region}:{account}:sim2real-behav-sns-fpcx-{Guid.NewGuid().ToString("N")[..8]}";

        var (atLimit, over) = (Policy(5, 5, 6), Policy(5, 5, 7));
        var sub = (await sns.SubscribeAsync(new SubscribeRequest
        {
            TopicArn = topic, Protocol = "sqs", Endpoint = QueueArn(), ReturnSubscriptionArn = true,
            Attributes = new() { ["FilterPolicy"] = PyDumps(Policy(2)) },
        })).SubscriptionArn;
        ctx.OnCleanup(() => sns.UnsubscribeAsync(new UnsubscribeRequest { SubscriptionArn = sub }));

        await sns.SetSubscriptionAttributesAsync(new SetSubscriptionAttributesRequest
        {
            SubscriptionArn = sub, AttributeName = "FilterPolicy", AttributeValue = PyDumps(atLimit),
        });
        Ensure(JsonNode.DeepEquals(await Current(sns, sub), atLimit), "a 150-combination filter policy must be accepted and stored");

        var (code, message) = await Error(() => sns.SetSubscriptionAttributesAsync(new SetSubscriptionAttributesRequest
        {
            SubscriptionArn = sub, AttributeName = "FilterPolicy", AttributeValue = PyDumps(over),
        }));
        Ensure((code, message) == ("InvalidParameter", "Invalid parameter: FilterPolicy: Filter policy is too complex"),
            $"a 175-combination filter policy on SetSubscriptionAttributes gave '{code}': '{message}'");
        Ensure(JsonNode.DeepEquals(await Current(sns, sub), atLimit), "the refused policy replaced the subscription's previous one");

        (code, message) = await Error(() => sns.SubscribeAsync(new SubscribeRequest
        {
            TopicArn = topic, Protocol = "sqs", Endpoint = QueueArn(), ReturnSubscriptionArn = true,
            Attributes = new() { ["FilterPolicy"] = PyDumps(over) },
        }));
        Ensure((code, message) == ("InvalidParameter", "Invalid parameter: Attributes Reason: FilterPolicy: Filter policy is too complex"),
            $"a 175-combination filter policy on Subscribe gave '{code}': '{message}'");
        var subs = new List<string>();
        await foreach (var page in sns.Paginators.ListSubscriptionsByTopic(new ListSubscriptionsByTopicRequest { TopicArn = topic }).Responses)
        {
            subs.AddRange((page.Subscriptions ?? []).Select(s => s.SubscriptionArn));
        }

        Ensure(subs.SequenceEqual([sub]), $"the refused Subscribe changed the topic's subscriptions; it lists {subs.Count}");

        var second = (await sns.SubscribeAsync(new SubscribeRequest
        {
            TopicArn = topic, Protocol = "sqs", Endpoint = QueueArn(), ReturnSubscriptionArn = true,
            Attributes = new() { ["FilterPolicy"] = PyDumps(atLimit) },
        })).SubscriptionArn;
        ctx.OnCleanup(() => sns.UnsubscribeAsync(new UnsubscribeRequest { SubscriptionArn = second }));
        Ensure(JsonNode.DeepEquals(await Current(sns, second), atLimit), "Subscribe must accept a 150-combination filter policy");
    }

    /// <summary>(Code, Message) of the error <c>fn</c> raises; an unexpected success raises, naming it.</summary>
    private static async Task<(string? Code, string? Message)> Error<T>(Func<Task<T>> fn)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is null)
        {
            throw Fail($"expected the call to be refused, but it succeeded: {result}");
        }

        return (error.Code, error.Message);
    }

    /// <summary>One key per size, each matching that many distinct string values.</summary>
    private static JsonObject Policy(params int[] sizes)
    {
        var policy = new JsonObject();
        for (var i = 0; i < sizes.Length; i++)
        {
            var values = new JsonArray();
            for (var j = 0; j < sizes[i]; j++)
            {
                values.Add($"v{i}-{j}");
            }

            policy[$"k{i}"] = values;
        }

        return policy;
    }

    private static async Task<JsonNode?> Current(AmazonSimpleNotificationServiceClient sns, string sub) =>
        JsonNode.Parse((await sns.GetSubscriptionAttributesAsync(new GetSubscriptionAttributesRequest { SubscriptionArn = sub }))
            .Attributes!["FilterPolicy"]);

    /// <summary><c>json.dumps(value)</c> with Python's default separators (", " and ": ").</summary>
    private static string PyDumps(JsonNode? node) => node switch
    {
        null => "null",
        JsonObject o => "{" + string.Join(", ", o.Select(kv => $"{JsonSerializer.Serialize(kv.Key)}: {PyDumps(kv.Value)}")) + "}",
        JsonArray a => "[" + string.Join(", ", a.Select(PyDumps)) + "]",
        _ => node.ToJsonString(),
    };
}
