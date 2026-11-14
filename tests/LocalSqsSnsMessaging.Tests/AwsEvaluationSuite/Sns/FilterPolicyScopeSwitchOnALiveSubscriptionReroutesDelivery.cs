// Translated from behavioral/sns/check_filter_policy_scope_switch_on_a_live_subscription_reroutes_delivery.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Text.Json.Nodes;
using Amazon.SimpleNotificationService.Model;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sns;

/// <summary>sns.filter-policy-scope-switch-on-a-live-subscription-reroutes-delivery</summary>
[Category("AwsEvaluationSuite")]
public sealed class FilterPolicyScopeSwitchOnALiveSubscriptionReroutesDelivery
{
    private const string Scenario = "sns-filter-policy-scope-switch-on-a-live-sub";

    /// <summary>
    /// Switching only FilterPolicyScope on a live subscription re-points its unchanged policy:
    /// under MessageAttributes the attribute match is delivered and the body match withheld, and
    /// after switching to MessageBody the reverse holds.
    /// </summary>
    [Test]
    [DisplayName("sns.filter-policy-scope-switch-on-a-live-subscription-reroutes-delivery")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sns = ctx.Sns;
        var topicArn = await CreateTopic(ctx);
        var (twinUrl, twinArn) = await CreateQueueForTopic(ctx, topicArn);
        var (url, arn) = await CreateQueueForTopic(ctx, topicArn);
        await SubscribeAndConfirm(ctx, topicArn, twinArn, attributes: new() { ["RawMessageDelivery"] = "true" });
        var sub = await SubscribeAndConfirm(ctx, topicArn, arn,
            attributes: new() { ["RawMessageDelivery"] = "true", ["FilterPolicy"] = """{"kind": ["wanted"]}""" });

        async Task<(string ByAttr, string ByBody)> PublishPair(string tag)
        {
            var byAttr = $$"""{"kind": "unwanted", "id": "{{FidelityContext.UniqueName($"sim2real-attr-{tag}-")}}"}""";
            var byBody = $$"""{"kind": "wanted", "id": "{{FidelityContext.UniqueName($"sim2real-body-{tag}-")}}"}""";
            await sns.PublishAsync(new PublishRequest
            {
                TopicArn = topicArn,
                Message = byAttr,
                MessageAttributes = new() { ["kind"] = new() { DataType = "String", StringValue = "wanted" } },
            });
            await sns.PublishAsync(new PublishRequest { TopicArn = topicArn, Message = byBody });
            return (byAttr, byBody);
        }

        async Task<(string Withheld, List<string> Leaked)> OneSide(string tag, bool admitBody)
        {
            // The unfiltered twin bounds each absence by a delivery of the same publish.
            var (byAttr, byBody) = await PublishPair(tag);
            await ReceiveKeyed(ctx, twinUrl, m => m.Body, [byAttr, byBody], timeoutS: 60.0,
                description: "the unfiltered twin receives both messages");
            var (admitted, withheld) = admitBody ? (byBody, byAttr) : (byAttr, byBody);
            // Bodies read alongside the match count as leaks too, not only the later drain.
            var other = new List<string>();
            await ReceiveKeyed(ctx, url, m => m.Body, [admitted], timeoutS: 60.0,
                description: "the filtered subscription receives its match", other: other);
            return (withheld, [.. other, .. await Drain(ctx, url, windowS: 15.0)]);
        }

        var (_, leaked) = await OneSide("attrscope", admitBody: false);
        Ensure(leaked.Count == 0, $"under MessageAttributes scope the body-only match must be withheld; got {Repr(leaked)}");

        await sns.SetSubscriptionAttributesAsync(new SetSubscriptionAttributesRequest
        {
            SubscriptionArn = sub,
            AttributeName = "FilterPolicyScope",
            AttributeValue = "MessageBody",
        });
        var stored = await PollUntil(
            async () => (await sns.GetSubscriptionAttributesAsync(new GetSubscriptionAttributesRequest { SubscriptionArn = sub })).Attributes,
            a => a.GetValueOrDefault("FilterPolicyScope") == "MessageBody", timeoutS: 30.0, intervalS: 1.0,
            description: "the subscription reports FilterPolicyScope=MessageBody");
        Ensure(JsonNode.DeepEquals(JsonNode.Parse(stored["FilterPolicy"]), JsonNode.Parse("""{"kind": ["wanted"]}""")),
            $"switching the scope must leave the policy unchanged, got '{stored["FilterPolicy"]}'");

        // The switch is eventually consistent on the delivery path: wait until a body match is
        // admitted, then grade a fresh pair.
        var admittedAfterSwitch = false;
        for (var attempt = 0; attempt < 6; attempt++)
        {
            var (byAttr, byBody) = await PublishPair($"converge{attempt}");
            await ReceiveKeyed(ctx, twinUrl, m => m.Body, [byAttr, byBody], timeoutS: 60.0,
                description: "the unfiltered twin receives both messages");
            if ((await Drain(ctx, url, windowS: 10.0)).Contains(byBody))
            {
                admittedAfterSwitch = true;
                break;
            }
        }

        if (!admittedAfterSwitch)
        {
            throw Fail("after the scope switch the body match was never admitted");
        }

        await Drain(ctx, url, windowS: 5.0);

        (_, leaked) = await OneSide("bodyscope", admitBody: true);
        Ensure(leaked.Count == 0, $"under MessageBody scope the attribute-only match must be withheld; got {Repr(leaked)}");
    }

    private static string Repr(IEnumerable<string> items) => "[" + string.Join(", ", items.Select(s => $"'{s}'")) + "]";

    private static string Head(string s, int n) => s.Length <= n ? s : s[..n];

    private static List<Amazon.SimpleNotificationService.Model.Tag> TagsKv() =>
        [new() { Key = "sim2real", Value = Scenario }, new() { Key = "sim2real:managed", Value = "true" }];

    /// <summary>CreateTopic (tagged + prefix-named) and register DeleteTopic immediately.</summary>
    private static async Task<string> CreateTopic(FidelityContext ctx, bool fifo = false)
    {
        var sns = ctx.Sns;
        var name = FidelityContext.UniqueName("sim2real-behav-sns-t-");
        var attrs = new Dictionary<string, string>();
        if (fifo)
        {
            name += ".fifo";
            attrs["FifoTopic"] = "true";
        }

        var arn = (await sns.CreateTopicAsync(new CreateTopicRequest { Name = name, Attributes = attrs, Tags = TagsKv() })).TopicArn;
        ctx.OnCleanup(() => ctx.Sns.DeleteTopicAsync(new DeleteTopicRequest { TopicArn = arn }));
        return arn;
    }

    /// <summary>Create a real SQS queue whose policy lets exactly this topic send to it.</summary>
    private static async Task<(string Url, string Arn)> CreateQueueForTopic(FidelityContext ctx, string topicArn, bool fifo = false)
    {
        var sqs = ctx.Sqs;
        var name = FidelityContext.UniqueName("sim2real-behav-sns-q-");
        var attrs = new Dictionary<string, string>();
        if (fifo)
        {
            name += ".fifo";
            attrs["FifoQueue"] = "true";
        }

        var url = (await sqs.CreateQueueAsync(new CreateQueueRequest
        {
            QueueName = name,
            Attributes = attrs,
            Tags = new() { ["sim2real"] = Scenario, ["sim2real:managed"] = "true" },
        })).QueueUrl;
        ctx.OnCleanup(() => DeleteQueueIfPresent(ctx, url));
        var arn = (await sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest { QueueUrl = url, AttributeNames = ["QueueArn"] })).Attributes["QueueArn"];
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
            async () => (await sns.GetSubscriptionAttributesAsync(new GetSubscriptionAttributesRequest { SubscriptionArn = subArn }))
                .Attributes?.GetValueOrDefault("PendingConfirmation"),
            pending => pending == "false",
            timeoutS: timeoutS, intervalS: 1.0,
            description: "the sqs subscription leaves PendingConfirmation");
        return subArn;
    }

    /// <summary>Bounded retry; RAISES on the deadline so a timeout can never read as a pass.</summary>
    private static async Task<T> PollUntil<T>(Func<Task<T>> fn, Func<T, bool> pred, double timeoutS, double intervalS, string description)
    {
        var deadline = FidelityTime.StartNew();
        var value = await fn();
        while (!pred(value))
        {
            if (deadline.Elapsed >= TimeSpan.FromSeconds(timeoutS))
            {
                throw Fail($"{description}: not satisfied within {timeoutS}s, last value '{value}'");
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(intervalS));
            value = await fn();
        }

        return value;
    }

    /// <summary>
    /// Long-poll until a message keyed `key(msg)` exists for every wanted key; RAISES on the
    /// deadline. Each message is returned whole (Body plus MessageAttributes). Bodies outside
    /// `wanted` are appended to the caller-supplied `other` list when one is given.
    /// </summary>
    private static async Task<Dictionary<string, Message>> ReceiveKeyed(FidelityContext ctx, string queueUrl, Func<Message, string> key,
        HashSet<string> wanted, double timeoutS, string description, List<string>? other = null)
    {
        var sqs = ctx.Sqs;
        var got = new Dictionary<string, Message>();
        other ??= [];
        var deadline = FidelityTime.StartNew();
        while (!wanted.IsSubsetOf(got.Keys))
        {
            var resp = await sqs.ReceiveMessageAsync(new ReceiveMessageRequest
            {
                QueueUrl = queueUrl,
                MaxNumberOfMessages = 10,
                WaitTimeSeconds = 5,
                MessageAttributeNames = ["All"],
                MessageSystemAttributeNames = ["All"],
            });
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
                throw Fail($"{description}: {Repr(wanted.Except(got.Keys).Order(StringComparer.Ordinal))} never arrived within " +
                    $"{timeoutS}s; other bodies seen: {Repr(other)}");
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
