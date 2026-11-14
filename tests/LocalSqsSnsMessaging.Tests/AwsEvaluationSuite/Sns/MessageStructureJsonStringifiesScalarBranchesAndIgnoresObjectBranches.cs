// Translated from behavioral/sns/check_message_structure_json_stringifies_scalar_branches_and_ignores_object_branches.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Amazon.SimpleNotificationService.Model;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sns;

/// <summary>sns.message-structure-json-stringifies-scalar-branches-and-ignores-object-branches</summary>
[Category("AwsEvaluationSuite")]
public sealed class MessageStructureJsonStringifiesScalarBranchesAndIgnoresObjectBranches
{
    private const string Scenario = "sns-message-structure-json-stringifies-scala";
    private const string NoSuchQueue = "AWS.SimpleQueueService.NonExistentQueue";

    /// <summary>
    /// With MessageStructure=json a scalar branch is delivered as its JSON text, an object or null
    /// `sqs` branch falls back to `default`, an object/array `default` is refused as "No default
    /// entry", and any other MessageStructure delivers the body verbatim.
    /// </summary>
    [Test]
    [DisplayName("sns.message-structure-json-stringifies-scalar-branches-and-ignores-object-branches")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    [KnownFidelityGap(FidelityGap.SnsMessageStructure, "MessageStructure=json isn't validated: a document whose default branch is not a string is accepted")]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sns = ctx.Sns;
        var topicArn = await CreateTopic(ctx);
        var (url, arn) = await CreateQueueForTopic(ctx, topicArn);
        await SubscribeAndConfirm(ctx, topicArn, arn);

        var objDefault = FidelityContext.UniqueName("sim2real-behav-sns-ms-objbranch-");
        var nullDefault = FidelityContext.UniqueName("sim2real-behav-sns-ms-nullbranch-");
        var number = 100000000 + RandomNumberGenerator.GetInt32(800000000);
        var verbatim = PyDumps(new JsonObject { ["default"] = FidelityContext.UniqueName("sim2real-behav-sns-ms-verbatim-") });
        var cases = new List<(JsonObject Payload, string Expected)>
        {
            (new() { ["default"] = objDefault, ["sqs"] = new JsonObject { ["nested"] = 1 } }, objDefault),
            (new() { ["default"] = nullDefault, ["sqs"] = null }, nullDefault),
            (new() { ["default"] = "not-selected", ["sqs"] = number }, number.ToString(CultureInfo.InvariantCulture)),
            (new() { ["default"] = true }, "true"),
            (new() { ["default"] = null }, "null"),
            (new() { ["default"] = "not-selected-either", ["sqs"] = "" }, ""),
        };
        foreach (var (payload, _) in cases)
        {
            var resp = await sns.PublishAsync(new PublishRequest { TopicArn = topicArn, Message = PyDumps(payload), MessageStructure = "json" });
            Ensure(!string.IsNullOrEmpty(resp.MessageId), $"expected {PyRepr(payload)} to be accepted, got {resp}");
        }

        {
            var resp = await sns.PublishAsync(new PublishRequest { TopicArn = topicArn, Message = verbatim, MessageStructure = "string" });
            Ensure(!string.IsNullOrEmpty(resp.MessageId), $"MessageStructure='string' must be accepted, got {resp}");
        }

        foreach (var bad in new[] { new JsonObject { ["default"] = new JsonObject { ["x"] = 1 } }, new JsonObject { ["default"] = new JsonArray(1, 2) } })
        {
            var (code, message) = await Error(() => sns.PublishAsync(new PublishRequest
            {
                TopicArn = topicArn, Message = PyDumps(bad), MessageStructure = "json",
            }));
            Ensure((code, message) == ("InvalidParameter", "Invalid parameter: Message Structure - No default entry in JSON message body"),
                $"a non-scalar default {PyRepr(bad)} must be refused as having no default entry, got " +
                $"'{code}' '{message}'");
        }

        var wanted = cases.Select(c => c.Expected).ToHashSet();
        wanted.Add(verbatim);
        await ReceiveKeyed(ctx, url, EnvelopeMessage, wanted, timeoutS: 90.0,
            description: "each json-structured publish delivers its selected branch as " +
                         "text, and the 'string' publish delivers its body verbatim");
        var leaked = await Drain(ctx, url, windowS: 10.0);
        Ensure(leaked.Count == 0,
            $"exactly one notification per accepted publish; extra deliveries [{string.Join(", ", leaked.Select(b => $"'{b}'"))}]");
    }

    private static List<Amazon.SimpleNotificationService.Model.Tag> TagsKv() =>
        [new() { Key = "sim2real", Value = Scenario }, new() { Key = "sim2real:managed", Value = "true" }];

    /// <summary>CreateTopic (tagged + prefix-named) and register DeleteTopic immediately.</summary>
    private static async Task<string> CreateTopic(FidelityContext ctx, bool fifo = false)
    {
        var name = FidelityContext.UniqueName("sim2real-behav-sns-t-");
        var attrs = new Dictionary<string, string>();
        if (fifo)
        {
            name += ".fifo";
            attrs["FifoTopic"] = "true";
        }

        var arn = (await ctx.Sns.CreateTopicAsync(new CreateTopicRequest { Name = name, Attributes = attrs, Tags = TagsKv() })).TopicArn;
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
        var error = await FidelityContext.CaptureError(() => ctx.Sqs.DeleteQueueAsync(new DeleteQueueRequest { QueueUrl = url }));
        if (error is not null && error.Code != NoSuchQueue)
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

    /// <summary>(Error.Code, Error.Message) the call raises. An unexpected success RAISES.</summary>
    private static async Task<(string Code, string Message)> Error<T>(Func<Task<T>> fn)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is null)
        {
            throw Fail($"expected the call to be refused, but it succeeded: {result}");
        }

        return (error.Code ?? "", error.Message ?? "");
    }

    /// <summary>
    /// Long-poll until a message keyed `key(msg)` exists for every wanted key; RAISES on the
    /// deadline. Each message is returned whole (Body plus MessageAttributes).
    /// </summary>
    private static async Task<Dictionary<string, Message>> ReceiveKeyed(FidelityContext ctx, string queueUrl, Func<Message, string?> key,
        HashSet<string> wanted, double timeoutS, string description)
    {
        var sqs = ctx.Sqs;
        var got = new Dictionary<string, Message>();
        var other = new List<string>();
        var deadline = FidelityTime.StartNew();
        while (!wanted.IsSubsetOf(got.Keys))
        {
            var resp = await sqs.ReceiveMessageAsync(new ReceiveMessageRequest
            {
                QueueUrl = queueUrl, MaxNumberOfMessages = 10, WaitTimeSeconds = 5,
                MessageAttributeNames = ["All"], MessageSystemAttributeNames = ["All"],
            });
            foreach (var msg in resp.Messages ?? [])
            {
                await sqs.DeleteMessageAsync(new DeleteMessageRequest { QueueUrl = queueUrl, ReceiptHandle = msg.ReceiptHandle });
                var k = key(msg);
                if (k is not null && wanted.Contains(k) && !got.ContainsKey(k))
                {
                    got[k] = msg;
                }
                else
                {
                    other.Add(msg.Body[..Math.Min(160, msg.Body.Length)]);
                }
            }

            if (!wanted.IsSubsetOf(got.Keys) && deadline.Elapsed >= TimeSpan.FromSeconds(timeoutS))
            {
                throw Fail(
                    $"{description}: [{string.Join(", ", wanted.Except(got.Keys).Order(StringComparer.Ordinal).Select(w => $"'{w}'"))}] never arrived within " +
                    $"{timeoutS}s; other bodies seen: [{string.Join(", ", other.Select(o => $"'{o}'"))}]");
            }
        }

        return got;
    }

    /// <summary>The Message field of an SNS notification envelope, or None for a non-envelope body.</summary>
    private static string? EnvelopeMessage(Message msg)
    {
        JsonNode? body;
        try
        {
            body = JsonNode.Parse(msg.Body);
        }
        catch (JsonException)
        {
            return null;
        }

        if (body is JsonObject obj && obj["Type"] is JsonValue type && type.GetValueKind() == JsonValueKind.String && type.GetValue<string>() == "Notification")
        {
            return obj["Message"] is JsonValue m && m.GetValueKind() == JsonValueKind.String ? m.GetValue<string>() : obj["Message"]?.ToJsonString();
        }

        return null;
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
                bodies.Add(msg.Body[..Math.Min(300, msg.Body.Length)]);
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
