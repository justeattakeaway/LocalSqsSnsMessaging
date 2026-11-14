// Translated from behavioral/sns/check_filter_policy_message_body_scope_matches_array_members_and_withholds_non_json.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Globalization;
using System.Text.Json.Nodes;
using Amazon.SimpleNotificationService.Model;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sns;

/// <summary>sns.filter-policy-message-body-scope-matches-array-members-and-withholds-non-json</summary>
[Category("AwsEvaluationSuite")]
public sealed class FilterPolicyMessageBodyScopeMatchesArrayMembersAndWithholdsNonJson
{
    private const string Scenario = "sns-filter-body-array";

    /// <summary>
    /// With FilterPolicyScope=MessageBody, a policy value matches a JSON array in the body when
    /// ANY member matches, matches a scalar equal to it, and withholds a body that is not JSON at
    /// all or lacks the key.
    /// </summary>
    [Test]
    [DisplayName("sns.filter-policy-message-body-scope-matches-array-members-and-withholds-non-json")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sns = ctx.Sns;
        var topicArn = await CreateTopic(ctx);

        var (controlUrl, controlArn) = await CreateQueueForTopic(ctx, topicArn);
        await SubscribeAndConfirm(ctx, topicArn, controlArn, attributes: new() { ["RawMessageDelivery"] = "true" });

        var policy = new JsonObject { ["order"] = new JsonObject { ["items"] = new JsonArray("book") } };
        var (url, arn) = await CreateQueueForTopic(ctx, topicArn);
        var sub = await SubscribeAndConfirm(ctx, topicArn, arn, attributes: new()
        {
            ["RawMessageDelivery"] = "true", ["FilterPolicyScope"] = "MessageBody", ["FilterPolicy"] = policy.ToJsonString(),
        });
        var attrs = (await sns.GetSubscriptionAttributesAsync(new GetSubscriptionAttributesRequest { SubscriptionArn = sub })).Attributes ?? [];
        Ensure(attrs.GetValueOrDefault("FilterPolicyScope") == "MessageBody",
            $"FilterPolicyScope read back as '{attrs.GetValueOrDefault("FilterPolicyScope")}', not 'MessageBody'");

        // M1 (array member) and M3 (scalar) match; M2, M4 (not JSON) and M5 (no key) are withheld.
        // M4 contains the word "book" so substring matching on the raw body would wrongly deliver it.
        var ids = Enumerable.Range(1, 5).Select(i => FidelityContext.UniqueName($"sim2real-behav-bodyarr-m{i}-")).ToList();
        // json.dumps layout: ", " and ": " separators.
        string[] bodies =
        [
            $$$"""{"id": "{{{ids[0]}}}", "order": {"items": ["pen", "book"]}}""",
            $$$"""{"id": "{{{ids[1]}}}", "order": {"items": ["pen"]}}""",
            $$$"""{"id": "{{{ids[2]}}}", "order": {"items": "book"}}""",
            $"{ids[3]} order items book",
            $$$"""{"id": "{{{ids[4]}}}", "order": {"total": 5}}""",
        ];

        var started = FidelityTime.UtcNow;
        foreach (var body in bodies)
        {
            await sns.PublishAsync(new PublishRequest { TopicArn = topicArn, Message = body });
        }

        await GradeMatrix(ctx, controlUrl, new() { ["body-scope"] = (url, [bodies[0], bodies[2]]) }, bodies.ToHashSet(), started);
    }

    private static string Repr(IEnumerable<string> items) => "[" + string.Join(", ", items.Select(i => $"'{i}'")) + "]";

    private static List<Amazon.SimpleNotificationService.Model.Tag> TagsKv() =>
        [new() { Key = "sim2real", Value = Scenario }, new() { Key = "sim2real:managed", Value = "true" }];

    /// <summary>CreateTopic (tagged + prefix-named) and register DeleteTopic immediately.</summary>
    private static async Task<string> CreateTopic(FidelityContext ctx)
    {
        var name = FidelityContext.UniqueName("sim2real-behav-sns-t-");
        var arn = (await ctx.Sns.CreateTopicAsync(new CreateTopicRequest { Name = name, Attributes = [], Tags = TagsKv() })).TopicArn;
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
        ctx.OnCleanup(() => ctx.Sqs.DeleteQueueAsync(url));
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
            timeoutS: timeoutS, intervalS: 2.0,
            description: "the new subscription leaves PendingConfirmation");
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
    /// Long-poll until every wanted body is seen; RAISES on the deadline.
    /// Returns {body: monotonic time first seen}. A body in <c>forbidden</c> that arrives raises at
    /// once: it is a leak, and swallowing it would hide what the caller is about to assert.
    /// </summary>
    private static async Task<Dictionary<string, DateTimeOffset>> AwaitBodies(FidelityContext ctx, string queueUrl, HashSet<string> wanted,
        double timeoutS, string description, HashSet<string>? forbidden = null)
    {
        var sqs = ctx.Sqs;
        var seen = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);
        var outstanding = new HashSet<string>(wanted, StringComparer.Ordinal);
        var deadline = FidelityTime.StartNew();
        while (outstanding.Count > 0)
        {
            var resp = await sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = queueUrl, MaxNumberOfMessages = 10, WaitTimeSeconds = 5 });
            foreach (var msg in resp.Messages ?? [])
            {
                await sqs.DeleteMessageAsync(new DeleteMessageRequest { QueueUrl = queueUrl, ReceiptHandle = msg.ReceiptHandle });
                var body = msg.Body;
                if (forbidden is { Count: > 0 } && forbidden.Contains(body))
                {
                    throw Fail($"{description}: a body that must never be delivered arrived: '{body}'");
                }

                if (outstanding.Remove(body))
                {
                    seen[body] = FidelityTime.UtcNow;
                }
            }

            if (outstanding.Count > 0 && deadline.Elapsed >= TimeSpan.FromSeconds(timeoutS))
            {
                throw Fail($"{description}: {Repr(outstanding.Order(StringComparer.Ordinal))} never arrived within {timeoutS}s");
            }
        }

        return seen;
    }

    /// <summary>
    /// Alternate long polls across the queues for one shared window and return every body
    /// each produced. The empty lists feed an equality-to-empty assertion.
    /// </summary>
    private static async Task<Dictionary<string, List<string>>> Drain(FidelityContext ctx, Dictionary<string, string> queues, double windowS)
    {
        var sqs = ctx.Sqs;
        var output = queues.Keys.ToDictionary(label => label, _ => new List<string>());
        var deadline = FidelityTime.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(windowS))
        {
            foreach (var (label, url) in queues)
            {
                var resp = await sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = url, MaxNumberOfMessages = 10, WaitTimeSeconds = 2 });
                foreach (var msg in resp.Messages ?? [])
                {
                    await sqs.DeleteMessageAsync(new DeleteMessageRequest { QueueUrl = url, ReceiptHandle = msg.ReceiptHandle });
                    output[label].Add(msg.Body);
                }
            }
        }

        return output;
    }

    /// <summary>
    /// Wait for the unfiltered control to receive everything, then require each arm to receive
    /// exactly its expected set and nothing more within a latency-calibrated window.
    /// </summary>
    private static async Task GradeMatrix(FidelityContext ctx, string controlUrl, Dictionary<string, (string Url, HashSet<string> Expected)> arms,
        HashSet<string> published, DateTimeOffset started)
    {
        var sqs = ctx.Sqs;
        var controlSeen = await AwaitBodies(ctx, controlUrl, published, timeoutS: 120.0,
            description: "the unfiltered control subscription must receive every published " +
                "message; without this barrier an absence at a filtered arm proves " +
                "nothing");
        var latency = Math.Max((controlSeen.Values.Max() - started).TotalSeconds, 1.0);
        var ceiling = Math.Max(60.0, (3.0 * latency) + 20.0);
        foreach (var (label, (url, expected)) in arms)
        {
            await AwaitBodies(ctx, url, expected, timeoutS: ceiling,
                description: $"the {label} arm must deliver exactly {Repr(expected.Order(StringComparer.Ordinal))}",
                forbidden: published.Except(expected).ToHashSet());
        }

        var window = Math.Max(3.0 * latency, 20.0);
        var leaked = await Drain(ctx, arms.ToDictionary(a => a.Key, a => a.Value.Url), windowS: window);
        Ensure(leaked.Values.All(l => l.Count == 0),
            $"after every arm had received its matching messages, a {window.ToString("F0", CultureInfo.InvariantCulture)}s window (>=3x " +
            $"the measured control latency of {latency.ToString("F1", CultureInfo.InvariantCulture)}s) must yield nothing more; got " +
            $"{{{string.Join(", ", leaked.Select(kv => $"'{kv.Key}': {Repr(kv.Value)}"))}}} -- a body here is a withheld message delivered late");
        foreach (var (label, (url, _)) in arms)
        {
            var approx = int.Parse((await sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest
            {
                QueueUrl = url, AttributeNames = ["ApproximateNumberOfMessages"],
            })).Attributes!["ApproximateNumberOfMessages"], CultureInfo.InvariantCulture);
            Ensure(approx == 0,
                "second observable for the absence: the " + label + " queue reports " +
                $"ApproximateNumberOfMessages={approx}, expected 0");
        }
    }
}
