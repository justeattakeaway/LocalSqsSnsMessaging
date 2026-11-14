// Translated from behavioral/sns/check_filter_policy_message_body_scope_cidr_and_or_conjunction_decide_delivery.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Text.Json;
using System.Text.Json.Nodes;
using Amazon.SimpleNotificationService.Model;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sns;

/// <summary>sns.filter-policy-message-body-scope-cidr-and-or-conjunction-decide-delivery</summary>
[Category("AwsEvaluationSuite")]
public sealed class FilterPolicyMessageBodyScopeCidrAndOrConjunctionDecideDelivery
{
    private const string Scenario = "sns-fp-body-scope-cidr-or";

    /// <summary>
    /// Under FilterPolicyScope=MessageBody, a top-level `$or` of a `cidr` match on `client.ip` and a
    /// value match on `client.vip` delivers a body that satisfies either branch and withholds one that
    /// satisfies neither; an unfiltered twin subscriber receives every body and bounds the wait.
    /// </summary>
    [Test]
    [DisplayName("sns.filter-policy-message-body-scope-cidr-and-or-conjunction-decide-delivery")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sns = ctx.Sns;
        var topicArn = await CreateTopic(ctx);
        var (twinUrl, twinArn) = await CreateQueueForTopic(ctx, topicArn);
        await SubscribeAndConfirm(ctx, topicArn, twinArn, attributes: new() { ["RawMessageDelivery"] = "true" });

        var policy = new JsonObject
        {
            ["$or"] = new JsonArray(
                new JsonObject { ["client"] = new JsonObject { ["ip"] = new JsonArray(new JsonObject { ["cidr"] = "10.0.0.0/8" }) } },
                new JsonObject { ["client"] = new JsonObject { ["vip"] = new JsonArray(true) } }),
        };
        var (url, arn) = await CreateQueueForTopic(ctx, topicArn);
        var sub = await SubscribeAndConfirm(
            ctx, topicArn, arn,
            attributes: new()
            {
                ["RawMessageDelivery"] = "true",
                ["FilterPolicyScope"] = "MessageBody",
                ["FilterPolicy"] = policy.ToJsonString(),
            });
        var stored = JsonNode.Parse((await sns.GetSubscriptionAttributesAsync(new GetSubscriptionAttributesRequest { SubscriptionArn = sub })).Attributes["FilterPolicy"]);
        Ensure(JsonNode.DeepEquals(stored, policy), $"the stored FilterPolicy must round-trip verbatim, got {stored?.ToJsonString()}");

        var marker = FidelityContext.UniqueName("sim2real-behav-sns-fpbody-cidror-");
        var bodies = new Dictionary<string, JsonObject>
        {
            ["cidr_match"] = new() { ["id"] = marker, ["which"] = "cidr_match", ["client"] = new JsonObject { ["ip"] = "10.1.2.3" } },
            ["vip_match"] = new() { ["id"] = marker, ["which"] = "vip_match", ["client"] = new JsonObject { ["vip"] = true } },
            ["neither"] = new()
            {
                ["id"] = marker,
                ["which"] = "neither",
                ["client"] = new JsonObject { ["ip"] = "192.168.1.1", ["vip"] = false },
            },
        };
        foreach (var body in bodies.Values)
        {
            await sns.PublishAsync(new PublishRequest { TopicArn = topicArn, Message = body.ToJsonString() });
        }

        var twinSeen = await ReceiveKeyed(
            ctx, twinUrl, Which, bodies.Keys.ToHashSet(),
            timeoutS: 90.0, description: "the unfiltered twin receives every body");
        var ids = twinSeen.Values.Select(m => (string?)JsonNode.Parse(m.Body)!["id"]).ToHashSet();
        Ensure(ids.SetEquals([marker]), "the twin's bodies must all carry this run's marker");

        var filteredOther = new List<string>();
        var filteredSeen = await ReceiveKeyed(
            ctx, url, Which, ["cidr_match", "vip_match"],
            timeoutS: 60.0,
            description: "the filtered subscription receives both $or branches that match",
            other: filteredOther);
        foreach (var which in new[] { "cidr_match", "vip_match" })
        {
            Ensure(JsonNode.DeepEquals(JsonNode.Parse(filteredSeen[which].Body), bodies[which]),
                $"the admitted body '{which}' must be delivered unchanged");
        }

        var leaked = filteredOther.Concat(await Drain(ctx, url, windowS: 15.0)).ToList();
        Ensure(leaked.Count == 0, $"a body matching neither $or branch must be withheld; leaked {Show(leaked)}");
    }

    /// <summary>json.loads(m["Body"])["which"]: throws JsonException (ValueError) or KeyNotFoundException (KeyError).</summary>
    private static string Which(Message m)
    {
        var node = JsonNode.Parse(m.Body)!.AsObject();
        return node.TryGetPropertyValue("which", out var which) ? (string)which! : throw new KeyNotFoundException("which");
    }

    private static string Show(IEnumerable<string> values) => "[" + string.Join(", ", values.Select(v => $"'{v}'")) + "]";

    private static string Head(string s, int n) => s.Length <= n ? s : s[..n];

    /// <summary>CreateTopic (tagged + prefix-named) and register DeleteTopic immediately.</summary>
    private static async Task<string> CreateTopic(FidelityContext ctx)
    {
        var sns = ctx.Sns;
        var name = FidelityContext.UniqueName("sim2real-behav-sns-t-");
        var arn = (await sns.CreateTopicAsync(new CreateTopicRequest { Name = name, Tags = [new() { Key = "sim2real", Value = Scenario }] })).TopicArn;
        ctx.OnCleanup(() => ctx.Sns.DeleteTopicAsync(new DeleteTopicRequest { TopicArn = arn }));
        return arn;
    }

    /// <summary>Create a real SQS queue whose policy lets exactly this topic send to it.</summary>
    private static async Task<(string Url, string Arn)> CreateQueueForTopic(FidelityContext ctx, string topicArn)
    {
        var sqs = ctx.Sqs;
        var name = FidelityContext.UniqueName("sim2real-behav-sns-q-");
        var url = (await sqs.CreateQueueAsync(new CreateQueueRequest { QueueName = name, Tags = new() { ["sim2real"] = Scenario } })).QueueUrl;
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
        Dictionary<string, string> attributes, double timeoutS = 60.0)
    {
        var sns = ctx.Sns;
        var subArn = (await sns.SubscribeAsync(new SubscribeRequest
        {
            TopicArn = topicArn,
            Protocol = "sqs",
            Endpoint = queueArn,
            ReturnSubscriptionArn = true,
            Attributes = attributes,
        })).SubscriptionArn;
        ctx.OnCleanup(() => ctx.Sns.UnsubscribeAsync(new UnsubscribeRequest { SubscriptionArn = subArn }));
        await PollUntil(
            async () => (await sns.GetSubscriptionAttributesAsync(new GetSubscriptionAttributesRequest { SubscriptionArn = subArn })).Attributes?.GetValueOrDefault("PendingConfirmation"),
            pending => pending == "false",
            timeoutS: timeoutS, intervalS: 1.0,
            description: "the sqs subscription leaves PendingConfirmation");
        return subArn;
    }

    /// <summary>Bounded retry; RAISES on the deadline so a timeout can never read as a pass.</summary>
    private static async Task<string?> PollUntil(Func<Task<string?>> fn, Func<string?, bool> pred, double timeoutS, double intervalS, string description)
    {
        var deadline = FidelityTime.StartNew();
        var value = await fn();
        while (!pred(value))
        {
            if (deadline.Elapsed >= TimeSpan.FromSeconds(timeoutS))
            {
                throw Fail($"{description}: not satisfied within {timeoutS}s, last value {(value is null ? "None" : $"'{value}'")}");
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(intervalS));
            value = await fn();
        }

        return value;
    }

    /// <summary>
    /// Long-poll until a message keyed key(msg) exists for every wanted key; RAISES on the
    /// deadline. Each message is returned whole; every other body received is appended to other,
    /// so a caller can assert nothing unwanted arrived while it waited.
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
            var resp = await sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = queueUrl, MaxNumberOfMessages = 10, WaitTimeSeconds = 5 });
            foreach (var msg in resp.Messages ?? [])
            {
                await sqs.DeleteMessageAsync(new DeleteMessageRequest { QueueUrl = queueUrl, ReceiptHandle = msg.ReceiptHandle });
                string k;
                try
                {
                    k = key(msg);
                }
                catch (Exception ex) when (ex is JsonException or KeyNotFoundException)
                {
                    other.Add(Head(msg.Body, 160));
                    continue;
                }

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
