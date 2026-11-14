// Translated from behavioral/sns/check_message_structure_json_selects_the_protocol_body_and_falls_back_to_default.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Text.Json;
using System.Text.Json.Nodes;
using Amazon.SimpleNotificationService.Model;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sns;

/// <summary>sns.message-structure-json-selects-the-protocol-body-and-falls-back-to-default</summary>
[Category("AwsEvaluationSuite")]
public sealed class MessageStructureJsonSelectsTheProtocolBodyAndFallsBackToDefault
{
    private const string Scenario = "sns-json-structure";

    /// <summary>
    /// MessageStructure=json delivers the "sqs" body to an SQS subscriber when present, else
    /// "default"; an unrecognised top-level key is ignored, not rejected.
    /// Reference: https://docs.aws.amazon.com/sns/latest/api/API_Publish.html
    /// </summary>
    [Test]
    [DisplayName("sns.message-structure-json-selects-the-protocol-body-and-falls-back-to-default")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sns = ctx.Sns;
        var topicArn = await CreateTopic(ctx);
        var (queueUrl, queueArn) = await CreateQueueForTopic(ctx, topicArn);
        // Not RawMessageDelivery: reading the envelope keeps the raw-delivery interaction out.
        await SubscribeAndConfirm(ctx, topicArn, queueArn);

        // Leg 1 — a protocol key present for this subscription's protocol wins over default.
        var sqsWins = FidelityContext.UniqueName("sim2real-behav-jsonstruct-sqs-branch-");
        var defaultLoses = FidelityContext.UniqueName("sim2real-behav-jsonstruct-default-loses-");
        // Leg 2 — no key for this protocol, so default is the fallback.
        var defaultWins = FidelityContext.UniqueName("sim2real-behav-jsonstruct-default-wins-");
        var emailNever = FidelityContext.UniqueName("sim2real-behav-jsonstruct-email-never-");
        // Leg 3 — an unrecognised top-level key is ignored, and the sqs key still wins.
        var sqsWinsAgain = FidelityContext.UniqueName("sim2real-behav-jsonstruct-sqs-again-");
        var defaultLosesAgain = FidelityContext.UniqueName("sim2real-behav-jsonstruct-default-loses-again-");
        var bogusNever = FidelityContext.UniqueName("sim2real-behav-jsonstruct-bogus-never-");

        var payloads = new List<JsonObject>
        {
            new() { ["default"] = defaultLoses, ["sqs"] = sqsWins },
            new() { ["default"] = defaultWins, ["email"] = emailNever },
            new() { ["default"] = defaultLosesAgain, ["sqs"] = sqsWinsAgain, ["not-a-transport-protocol"] = bogusNever },
        };
        foreach (var payload in payloads)
        {
            var resp = await sns.PublishAsync(new PublishRequest { TopicArn = topicArn, Message = PyDumps(payload), MessageStructure = "json" });
            Ensure(!string.IsNullOrEmpty(resp.MessageId),
                $"expected a MessageId for the json-structured publish [{string.Join(", ", payload.Select(kv => $"'{kv.Key}'").Order(StringComparer.Ordinal))}]; got " +
                $"{resp} — leg 3's unrecognised key must be IGNORED, not rejected");
        }

        var expected = new HashSet<string> { sqsWins, defaultWins, sqsWinsAgain };
        var forbidden = new HashSet<string> { defaultLoses, emailNever, defaultLosesAgain, bogusNever };
        var envelopes = await AwaitEnvelopes(ctx, queueUrl, expected, timeoutS: 120.0,
            description: "MessageStructure=json must deliver the sqs branch when present (leg 1 and 3) and " +
                         "the default branch when it is not (leg 2); any of the discarded branches arriving " +
                         "instead is a wrong selection, not a slow one",
            forbidden: forbidden);

        foreach (var marker in expected.Order(StringComparer.Ordinal))
        {
            // Message == marker holds by construction; selection exactness is carried by
            // `expected` / `forbidden` above.
            var envelope = envelopes[marker];
            Ensure((string?)envelope["Type"] == "Notification",
                $"expected Type 'Notification' on the delivered envelope for '{marker}', got " +
                $"'{envelope["Type"]}'");
            Ensure((string?)envelope["TopicArn"] == topicArn,
                $"expected TopicArn '{topicArn}' on the envelope for '{marker}', got " +
                $"'{envelope["TopicArn"]}'");
        }

        // Nothing else may arrive: a target that delivers BOTH branches of a json payload would
        // otherwise satisfy every assertion above.
        var leaked = await Drain(ctx, queueUrl, windowS: 30.0);
        Ensure(leaked.Count == 0,
            "exactly three notifications must result from three json-structured publishes; a 30s " +
            $"window after all three arrived produced [{string.Join(", ", leaked.Select(b => $"'{b}'"))}]");
    }

    /// <summary><c>str(value)</c> of a parsed JSON value, or <see langword="null"/> for a missing one.</summary>
    private static string? Text(JsonNode? node) =>
        node is JsonValue v && v.GetValueKind() == JsonValueKind.String ? v.GetValue<string>() : node?.ToJsonString();

    /// <summary>
    /// Long-poll until a notification envelope has arrived for every wanted Message value.
    /// RAISES on the deadline, and RAISES immediately on a forbidden Message value — a wrong
    /// branch is a failure, not something to keep waiting through.
    /// </summary>
    private static async Task<Dictionary<string, JsonObject>> AwaitEnvelopes(FidelityContext ctx, string queueUrl, HashSet<string> wanted,
        double timeoutS, string description, HashSet<string> forbidden)
    {
        var sqs = ctx.Sqs;
        var found = new Dictionary<string, JsonObject>();
        var outstanding = new HashSet<string>(wanted);
        var others = new List<string>();
        var deadline = FidelityTime.StartNew();
        while (outstanding.Count > 0)
        {
            var resp = await sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = queueUrl, MaxNumberOfMessages = 10, WaitTimeSeconds = 5 });
            foreach (var msg in resp.Messages ?? [])
            {
                await sqs.DeleteMessageAsync(new DeleteMessageRequest { QueueUrl = queueUrl, ReceiptHandle = msg.ReceiptHandle });
                var envelope = JsonNode.Parse(msg.Body)!.AsObject();
                var message = envelope["Message"] is JsonValue mv && mv.GetValueKind() == JsonValueKind.String ? mv.GetValue<string>() : null;
                if (message is not null && forbidden.Contains(message))
                {
                    throw Fail($"{description}: the discarded branch '{message}' was delivered");
                }

                if (message is not null && outstanding.Remove(message))
                {
                    found[message] = envelope;
                }
                else
                {
                    var text = Text(envelope["Message"]) ?? "None";
                    others.Add(text[..Math.Min(60, text.Length)]);
                }
            }

            if (outstanding.Count > 0 && deadline.Elapsed >= TimeSpan.FromSeconds(timeoutS))
            {
                throw Fail(
                    $"{description}: [{string.Join(", ", outstanding.Order(StringComparer.Ordinal).Select(o => $"'{o}'"))}] never arrived within {timeoutS}s; " +
                    $"other Message values seen: [{string.Join(", ", others.Select(o => $"'{o}'"))}]");
            }
        }

        return found;
    }

    private static List<Amazon.SimpleNotificationService.Model.Tag> TagsKv() =>
        [new() { Key = "sim2real", Value = Scenario }, new() { Key = "sim2real:managed", Value = "true" }];

    /// <summary>CreateTopic (tagged + prefix-named) and register DeleteTopic immediately.</summary>
    private static async Task<string> CreateTopic(FidelityContext ctx, Dictionary<string, string>? attributes = null)
    {
        var name = FidelityContext.UniqueName("sim2real-behav-sns-t-");
        var arn = (await ctx.Sns.CreateTopicAsync(new CreateTopicRequest
        {
            Name = name, Attributes = new Dictionary<string, string>(attributes ?? []), Tags = TagsKv(),
        })).TopicArn;
        ctx.OnCleanup(() => ctx.Sns.DeleteTopicAsync(new DeleteTopicRequest { TopicArn = arn }));
        return arn;
    }

    /// <summary>Create a real SQS queue whose policy lets exactly this topic publish to it.</summary>
    private static async Task<(string Url, string Arn)> CreateQueueForTopic(FidelityContext ctx, string topicArn)
    {
        var sqs = ctx.Sqs;
        var name = FidelityContext.UniqueName("sim2real-behav-sns-q-");
        var url = (await sqs.CreateQueueAsync(new CreateQueueRequest
        {
            QueueName = name,
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
            timeoutS: timeoutS,
            intervalS: 2.0,
            description: $"subscription {subArn} leaves PendingConfirmation");
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

    /// <summary>Long-poll for the whole window and return every body seen.</summary>
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
                bodies.Add(msg.Body);
            }
        }

        return bodies;
    }

    /// <summary><c>json.dumps(value)</c> with Python's default separators (", " and ": ").</summary>
    private static string PyDumps(JsonNode? node) => node switch
    {
        null => "null",
        JsonObject o => "{" + string.Join(", ", o.Select(kv => $"{JsonSerializer.Serialize(kv.Key)}: {PyDumps(kv.Value)}")) + "}",
        JsonArray a => "[" + string.Join(", ", a.Select(PyDumps)) + "]",
        _ => node.ToJsonString(),
    };
}
