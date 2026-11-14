// Translated from behavioral/sns/check_filter_policy_message_body_scope_numeric_exists_and_anything_but_decide_delivery.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Text.Json;
using System.Text.Json.Nodes;
using Amazon.Runtime;
using Amazon.SimpleNotificationService.Model;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;
using SqsMessage = Amazon.SQS.Model.Message;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sns;

/// <summary>sns.filter-policy-message-body-scope-numeric-exists-and-anything-but-decide-delivery</summary>
[Category("AwsEvaluationSuite")]
public sealed class FilterPolicyMessageBodyScopeNumericExistsAndAnythingButDecideDelivery
{
    private const string Scenario = "sns-fp-body-scope-operators";

    /// <summary>
    /// Under FilterPolicyScope=MessageBody, numeric, `exists` and `anything-but` decide delivery on nested JSON: a
    /// body with `order.total` > 100, no `order.status` and `tag` not "blocked" is delivered, one breaking any of
    /// the three is withheld; an unfiltered twin subscriber receives every body and bounds the wait.
    /// </summary>
    [Test]
    [DisplayName("sns.filter-policy-message-body-scope-numeric-exists-and-anything-but-decide-delivery")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sns = ctx.Sns;
        var topicArn = await CreateTopic(ctx);
        var (twinUrl, twinArn) = await CreateQueueForTopic(ctx, topicArn);
        await SubscribeAndConfirm(ctx, topicArn, twinArn, attributes: new() { ["RawMessageDelivery"] = "true" });

        var policy = JsonNode.Parse("""
            {
                "order": {"total": [{"numeric": [">", 100]}], "status": [{"exists": false}]},
                "tag": [{"anything-but": "blocked"}]
            }
            """)!;
        var (url, arn) = await CreateQueueForTopic(ctx, topicArn);
        var sub = await SubscribeAndConfirm(
            ctx, topicArn, arn,
            attributes: new()
            {
                ["RawMessageDelivery"] = "true", ["FilterPolicyScope"] = "MessageBody",
                ["FilterPolicy"] = policy.ToJsonString(Json),
            });
        var stored = JsonNode.Parse((await sns.GetSubscriptionAttributesAsync(new GetSubscriptionAttributesRequest { SubscriptionArn = sub })).Attributes["FilterPolicy"]);
        Ensure(JsonNode.DeepEquals(stored, policy), $"the stored FilterPolicy must round-trip verbatim, got {stored?.ToJsonString() ?? "None"}");

        var marker = FidelityContext.UniqueName("sim2real-behav-sns-fpbody-op-");
        var bodies = new Dictionary<string, JsonObject>
        {
            ["match"] = new() { ["id"] = marker, ["which"] = "match", ["order"] = new JsonObject { ["total"] = 150 }, ["tag"] = "ok" },
            ["fail_numeric"] = new() { ["id"] = marker, ["which"] = "fail_numeric", ["order"] = new JsonObject { ["total"] = 50 }, ["tag"] = "ok" },
            ["fail_exists"] = new()
            {
                ["id"] = marker, ["which"] = "fail_exists",
                ["order"] = new JsonObject { ["total"] = 150, ["status"] = "shipped" }, ["tag"] = "ok",
            },
            ["fail_anything_but"] = new()
            {
                ["id"] = marker, ["which"] = "fail_anything_but",
                ["order"] = new JsonObject { ["total"] = 150 }, ["tag"] = "blocked",
            },
        };
        foreach (var body in bodies.Values)
        {
            await sns.PublishAsync(new PublishRequest { TopicArn = topicArn, Message = body.ToJsonString(Json) });
        }

        var twinSeen = await ReceiveKeyed(
            ctx, twinUrl, m => (string?)JsonNode.Parse(m.Body)!["which"], bodies.Keys.ToHashSet(),
            timeoutS: 90.0, description: "the unfiltered twin receives every body");
        Ensure(twinSeen.Values.Select(m => (string?)JsonNode.Parse(m.Body)!["id"]).ToHashSet().SetEquals([marker]),
            "the twin's bodies must all carry this run's marker");

        var filteredSeen = await ReceiveKeyed(
            ctx, url, m => (string?)JsonNode.Parse(m.Body)!["which"], ["match"], timeoutS: 60.0,
            description: "the filtered subscription receives the one body satisfying all three operators");
        Ensure(JsonNode.DeepEquals(JsonNode.Parse(filteredSeen["match"].Body), bodies["match"]),
            "the admitted body must be delivered unchanged");

        var leaked = await Drain(ctx, url, windowS: 15.0);
        Ensure(leaked.Count == 0,
            "a body failing the numeric, exists or anything-but operator must be withheld; " +
            $"leaked {Repr(leaked)}");
    }

    // json.dumps escapes only quotes, backslashes and control characters in ASCII text.
    private static readonly JsonSerializerOptions Json = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private static string Repr(IEnumerable<string> values) => $"[{string.Join(", ", values.Select(v => $"'{v}'"))}]";

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
        try
        {
            await ctx.Sqs.DeleteQueueAsync(url);
        }
        catch (AmazonServiceException exc) when (exc.ErrorCode == "AWS.SimpleQueueService.NonExistentQueue")
        {
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
    /// Long-poll until a message keyed key(msg) exists for every wanted key; RAISES on the
    /// deadline. Each message is returned whole.
    /// </summary>
    private static async Task<Dictionary<string, SqsMessage>> ReceiveKeyed(FidelityContext ctx, string queueUrl, Func<SqsMessage, string?> key,
        HashSet<string> wanted, double timeoutS, string description)
    {
        var sqs = ctx.Sqs;
        var got = new Dictionary<string, SqsMessage>();
        var other = new List<string>();
        var deadline = FidelityTime.StartNew();
        while (!wanted.IsSubsetOf(got.Keys))
        {
            var resp = await sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = queueUrl, MaxNumberOfMessages = 10, WaitTimeSeconds = 5 });
            foreach (var msg in resp.Messages ?? [])
            {
                await sqs.DeleteMessageAsync(new DeleteMessageRequest { QueueUrl = queueUrl, ReceiptHandle = msg.ReceiptHandle });
                string? k;
                try
                {
                    k = key(msg);
                }
                catch (Exception e) when (e is JsonException or InvalidOperationException or NullReferenceException)
                {
                    other.Add(Head(msg.Body, 160));
                    continue;
                }

                if (k is not null && wanted.Contains(k) && !got.ContainsKey(k))
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
