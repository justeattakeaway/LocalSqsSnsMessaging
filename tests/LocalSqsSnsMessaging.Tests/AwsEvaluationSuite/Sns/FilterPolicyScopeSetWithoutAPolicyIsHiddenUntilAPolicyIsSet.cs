// Translated from behavioral/sns/check_filter_policy_scope_set_without_a_policy_is_hidden_until_a_policy_is_set.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Text.Json.Nodes;
using Amazon.SimpleNotificationService.Model;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;
using SnsMessageAttributeValue = Amazon.SimpleNotificationService.Model.MessageAttributeValue;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sns;

/// <summary>sns.filter-policy-scope-set-without-a-policy-is-hidden-until-a-policy-is-set</summary>
[Category("AwsEvaluationSuite")]
public sealed class FilterPolicyScopeSetWithoutAPolicyIsHiddenUntilAPolicyIsSet
{
    private const string Scenario = "sns-filter-policy-scope-set-without-a-policy";

    /// <summary>
    /// A FilterPolicyScope set without a FilterPolicy is not reported by GetSubscriptionAttributes
    /// but is kept: a FilterPolicy set later reads back under scope MessageBody and filters on
    /// the message body.
    /// </summary>
    [Test]
    [DisplayName("sns.filter-policy-scope-set-without-a-policy-is-hidden-until-a-policy-is-set")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    [KnownFidelityGap(FidelityGap.SnsSubscriptionAttributes, "A FilterPolicyScope given at Subscribe without a policy makes GetSubscriptionAttributes report FilterPolicy '' instead of no Filter* attributes")]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sns = ctx.Sns;
        var topicArn = await CreateTopic(ctx);
        var (twinUrl, twinArn) = await CreateQueueForTopic(ctx, topicArn);
        var (laterUrl, laterArn) = await CreateQueueForTopic(ctx, topicArn);
        var (_, earlyArn) = await CreateQueueForTopic(ctx, topicArn);
        Dictionary<string, string> Raw() => new() { ["RawMessageDelivery"] = "true" };
        await SubscribeAndConfirm(ctx, topicArn, twinArn, attributes: Raw());
        var later = await SubscribeAndConfirm(ctx, topicArn, laterArn, attributes: Raw());
        var earlyAttrs = Raw();
        earlyAttrs["FilterPolicyScope"] = "MessageBody";
        var early = await SubscribeAndConfirm(ctx, topicArn, earlyArn, attributes: earlyAttrs);

        async Task<Dictionary<string, string>> FilterKeys(string subArn) =>
            ((await sns.GetSubscriptionAttributesAsync(new GetSubscriptionAttributesRequest { SubscriptionArn = subArn })).Attributes ?? [])
                .Where(kv => kv.Key.StartsWith("Filter", StringComparison.Ordinal))
                .ToDictionary(kv => kv.Key, kv => kv.Value);

        Ensure((await FilterKeys(early)).Count == 0,
            $"a scope given at Subscribe without a policy must not be reported, got {Show(await FilterKeys(early))}");
        await sns.SetSubscriptionAttributesAsync(new SetSubscriptionAttributesRequest
        {
            SubscriptionArn = later,
            AttributeName = "FilterPolicyScope",
            AttributeValue = "MessageBody",
        });
        Ensure((await FilterKeys(later)).Count == 0,
            $"a scope set without a policy must not be reported, got {Show(await FilterKeys(later))}");

        var policy = new JsonObject { ["kind"] = new JsonArray("wanted") };
        foreach (var sub in new[] { later, early })
        {
            await sns.SetSubscriptionAttributesAsync(new SetSubscriptionAttributesRequest
            {
                SubscriptionArn = sub,
                AttributeName = "FilterPolicy",
                AttributeValue = policy.ToJsonString(),
            });
            var got = await PollUntil(() => FilterKeys(sub), a => a.ContainsKey("FilterPolicy"),
                timeoutS: 30.0, intervalS: 1.0,
                description: "the FilterPolicy becomes readable");
            Ensure(JsonNode.DeepEquals(JsonNode.Parse(got["FilterPolicy"]), policy) && got.GetValueOrDefault("FilterPolicyScope") == "MessageBody",
                $"the hidden scope must govern the policy set later: expected scope 'MessageBody', " +
                $"got {Show(got)}");
        }

        var byAttr = new JsonObject { ["kind"] = "unwanted", ["id"] = FidelityContext.UniqueName("sim2real-attr-") }.ToJsonString();
        var byBody = new JsonObject { ["kind"] = "wanted", ["id"] = FidelityContext.UniqueName("sim2real-body-") }.ToJsonString();
        List<string> bodies = [];
        for (var attempt = 0; attempt < 4; attempt++)
        {
            await sns.PublishAsync(new PublishRequest
            {
                TopicArn = topicArn,
                Message = byAttr,
                MessageAttributes = new() { ["kind"] = new SnsMessageAttributeValue { DataType = "String", StringValue = "wanted" } },
            });
            await sns.PublishAsync(new PublishRequest { TopicArn = topicArn, Message = byBody });
            await ReceiveKeyed(ctx, twinUrl, m => m.Body, [byAttr, byBody], timeoutS: 60.0,
                description: "the unfiltered twin receives both messages");
            bodies = await Drain(ctx, laterUrl, windowS: 10.0);
            if (bodies.Count > 0)
            {
                break;
            }
        }

        Ensure(bodies.SequenceEqual([byBody]),
            $"the policy must filter on the message body (the hidden scope): expected only the " +
            $"body match, got {Show(bodies)}");
    }

    private static string Show(IEnumerable<string> values) => "[" + string.Join(", ", values.Select(v => $"'{v}'")) + "]";

    private static string Show(Dictionary<string, string> d) => "{" + string.Join(", ", d.Select(kv => $"'{kv.Key}': '{kv.Value}'")) + "}";

    private static string Head(string s, int n) => s.Length <= n ? s : s[..n];

    private static List<Amazon.SimpleNotificationService.Model.Tag> TagsKv() =>
        [new() { Key = "sim2real", Value = Scenario }, new() { Key = "sim2real:managed", Value = "true" }];

    /// <summary>CreateTopic (tagged + prefix-named) and register DeleteTopic immediately.</summary>
    private static async Task<string> CreateTopic(FidelityContext ctx)
    {
        var sns = ctx.Sns;
        var name = FidelityContext.UniqueName("sim2real-behav-sns-t-");
        var arn = (await sns.CreateTopicAsync(new CreateTopicRequest { Name = name, Attributes = [], Tags = TagsKv() })).TopicArn;
        ctx.OnCleanup(() => ctx.Sns.DeleteTopicAsync(new DeleteTopicRequest { TopicArn = arn }));
        return arn;
    }

    /// <summary>Create a real SQS queue whose policy lets exactly this topic send to it.</summary>
    private static async Task<(string Url, string Arn)> CreateQueueForTopic(FidelityContext ctx, string topicArn)
    {
        var sqs = ctx.Sqs;
        var name = FidelityContext.UniqueName("sim2real-behav-sns-q-");
        var url = (await sqs.CreateQueueAsync(new CreateQueueRequest
        {
            QueueName = name,
            Attributes = [],
            Tags = new() { ["sim2real"] = Scenario, ["sim2real:managed"] = "true" },
        })).QueueUrl;
        ctx.OnCleanup(() => DeleteQueueIfPresent(ctx, url));
        var arn = (await sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest { QueueUrl = url, AttributeNames = ["QueueArn"] })).Attributes!["QueueArn"];
        var policy = new JsonObject
        {
            ["Version"] = "2012-10-17",
            ["Statement"] = new JsonArray(new JsonObject
            {
                ["Effect"] = "Allow",
                ["Principal"] = new JsonObject { ["Service"] = "sns.amazonaws.com" },
                ["Action"] = "sqs:SendMessage",
                ["Resource"] = arn,
                ["Condition"] = new JsonObject { ["ArnEquals"] = new JsonObject { ["aws:SourceArn"] = topicArn } },
            }),
        };
        await sqs.SetQueueAttributesAsync(new SetQueueAttributesRequest { QueueUrl = url, Attributes = new() { ["Policy"] = policy.ToJsonString() } });
        return (url, arn);
    }

    /// <summary>DeleteQueue; a queue the check itself already deleted is the one tolerated outcome.</summary>
    private static async Task DeleteQueueIfPresent(FidelityContext ctx, string url)
    {
        var error = await FidelityContext.CaptureError(() => ctx.Sqs.DeleteQueueAsync(url));
        if (error is not null && error.Code != "AWS.SimpleQueueService.NonExistentQueue")
        {
            throw error.Exception;
        }
    }

    /// <summary>Subscribe an SQS queue and poll GetSubscriptionAttributes until it is confirmed.</summary>
    private static async Task<string> SubscribeAndConfirm(FidelityContext ctx, string topicArn, string queueArn,
        Dictionary<string, string>? attributes = null, double timeoutS = 60.0)
    {
        var sns = ctx.Sns;
        var request = new SubscribeRequest { TopicArn = topicArn, Protocol = "sqs", Endpoint = queueArn, ReturnSubscriptionArn = true };
        if (attributes is { Count: > 0 })
        {
            request.Attributes = attributes;
        }

        var subArn = (await sns.SubscribeAsync(request)).SubscriptionArn;
        ctx.OnCleanup(() => ctx.Sns.UnsubscribeAsync(new UnsubscribeRequest { SubscriptionArn = subArn }));
        await PollUntil(
            async () => (await sns.GetSubscriptionAttributesAsync(new GetSubscriptionAttributesRequest { SubscriptionArn = subArn })).Attributes?.GetValueOrDefault("PendingConfirmation"),
            pending => pending == "false",
            timeoutS: timeoutS, intervalS: 1.0,
            description: "the sqs subscription leaves PendingConfirmation",
            show: v => v is null ? "None" : $"'{v}'");
        return subArn;
    }

    /// <summary>Bounded retry; RAISES on the deadline so a timeout can never read as a pass.</summary>
    private static async Task<T> PollUntil<T>(Func<Task<T>> fn, Func<T, bool> pred, double timeoutS, double intervalS, string description, Func<T, string>? show = null)
    {
        var deadline = FidelityTime.StartNew();
        var value = await fn();
        while (!pred(value))
        {
            if (deadline.Elapsed >= TimeSpan.FromSeconds(timeoutS))
            {
                var shown = show is not null ? show(value) : value is Dictionary<string, string> d ? Show(d) : $"{value}";
                throw Fail($"{description}: not satisfied within {timeoutS}s, last value {shown}");
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(intervalS));
            value = await fn();
        }

        return value;
    }

    /// <summary>
    /// Long-poll until a message keyed key(msg) exists for every wanted key; RAISES on the
    /// deadline. Each message is returned whole (Body plus MessageAttributes).
    /// </summary>
    private static async Task<Dictionary<string, Message>> ReceiveKeyed(FidelityContext ctx, string queueUrl, Func<Message, string> key,
        HashSet<string> wanted, double timeoutS, string description)
    {
        var sqs = ctx.Sqs;
        var got = new Dictionary<string, Message>();
        var other = new List<string>();
        var deadline = FidelityTime.StartNew();
        while (!wanted.IsSubsetOf(got.Keys))
        {
#pragma warning disable CS0618 // The check deliberately uses the legacy AttributeNames parameter.
            var resp = await sqs.ReceiveMessageAsync(new ReceiveMessageRequest
            {
                QueueUrl = queueUrl,
                MaxNumberOfMessages = 10,
                WaitTimeSeconds = 5,
                MessageAttributeNames = ["All"],
                AttributeNames = ["All"],
            });
#pragma warning restore CS0618
            foreach (var msg in resp.Messages ?? [])
            {
                await sqs.DeleteMessageAsync(new DeleteMessageRequest { QueueUrl = queueUrl, ReceiptHandle = msg.ReceiptHandle });
                var k = key(msg);
                if (wanted.Contains(k) && !got.ContainsKey(k))
                {
                    got[k] = msg;
                }
                else
                {
                    other.Add(Head(msg.Body, 160));
                }
            }

            if (!wanted.IsSubsetOf(got.Keys) && deadline.Elapsed >= TimeSpan.FromSeconds(timeoutS))
            {
                throw Fail($"{description}: {Show(wanted.Except(got.Keys).Order(StringComparer.Ordinal))} never arrived within " +
                    $"{timeoutS}s; other bodies seen: {Show(other)}");
            }
        }

        return got;
    }

    /// <summary>
    /// Long-poll for the whole window and return EVERY body seen, so the caller can assert on
    /// leakage rather than discard it.
    /// </summary>
    private static async Task<List<string>> Drain(FidelityContext ctx, string queueUrl, double windowS)
    {
        var sqs = ctx.Sqs;
        var bodies = new List<string>();
        var deadline = FidelityTime.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(windowS))
        {
            var resp = await sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = queueUrl, MaxNumberOfMessages = 10, WaitTimeSeconds = 5 });
            foreach (var msg in resp.Messages ?? [])
            {
                await sqs.DeleteMessageAsync(new DeleteMessageRequest { QueueUrl = queueUrl, ReceiptHandle = msg.ReceiptHandle });
                bodies.Add(Head(msg.Body, 300));
            }
        }

        return bodies;
    }
}
