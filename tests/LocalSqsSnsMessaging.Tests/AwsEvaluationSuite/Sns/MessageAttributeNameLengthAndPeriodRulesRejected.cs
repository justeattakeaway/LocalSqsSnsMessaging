// Translated from behavioral/sns/check_message_attribute_name_length_and_period_rules_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Text.Json.Nodes;
using Amazon.SimpleNotificationService.Model;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;
using SnsMessageAttributeValue = Amazon.SimpleNotificationService.Model.MessageAttributeValue;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sns;

/// <summary>sns.message-attribute-name-length-and-period-rules-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class MessageAttributeNameLengthAndPeriodRulesRejected
{
    private const string Check = "message-attribute-name-length-and-period-rules-rejected";

    /// <summary>
    /// Publish refuses a 257-char attribute name and leading, trailing or doubled periods with
    /// ParameterValueInvalid, delivering none; a 256-char name and "a.b.c" are delivered intact.
    /// </summary>
    [Test]
    [DisplayName("sns.message-attribute-name-length-and-period-rules-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    [KnownFidelityGap(FidelityGap.SnsValidation, "Publish accepts message attribute names over 256 characters or with leading, trailing or repeated periods")]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sns = ctx.Sns;
        var sqs = ctx.Sqs;
        var topic = (await sns.CreateTopicAsync(new CreateTopicRequest
        {
            Name = FidelityContext.UniqueName("sim2real-behav-sns-attrname-"),
            Tags = [new() { Key = "sim2real", Value = Check }],
        })).TopicArn;
        ctx.OnCleanup(() => sns.DeleteTopicAsync(new DeleteTopicRequest { TopicArn = topic }));
        var url = (await sqs.CreateQueueAsync(new CreateQueueRequest
        {
            QueueName = FidelityContext.UniqueName("sim2real-behav-sns-attrname-"),
            Tags = new() { ["sim2real"] = Check },
        })).QueueUrl;
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(url));
        var qArn = (await sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest { QueueUrl = url, AttributeNames = ["QueueArn"] })).Attributes!["QueueArn"];
        var policy = new JsonObject
        {
            ["Version"] = "2012-10-17",
            ["Statement"] = new JsonArray(new JsonObject
            {
                ["Effect"] = "Allow",
                ["Principal"] = new JsonObject { ["Service"] = "sns.amazonaws.com" },
                ["Action"] = "sqs:SendMessage",
                ["Resource"] = qArn,
                ["Condition"] = new JsonObject { ["ArnEquals"] = new JsonObject { ["aws:SourceArn"] = topic } },
            }),
        };
        await sqs.SetQueueAttributesAsync(new SetQueueAttributesRequest { QueueUrl = url, Attributes = new() { ["Policy"] = policy.ToJsonString() } });
        var sub = (await sns.SubscribeAsync(new SubscribeRequest
        {
            TopicArn = topic, Protocol = "sqs", Endpoint = qArn, ReturnSubscriptionArn = true,
        })).SubscriptionArn;
        ctx.OnCleanup(() => sns.UnsubscribeAsync(new UnsubscribeRequest { SubscriptionArn = sub }));

        static Dictionary<string, SnsMessageAttributeValue> Attr(string name) =>
            new() { [name] = new SnsMessageAttributeValue { DataType = "String", StringValue = "v" } };

        var longOk = new string('n', 256);
        var refusals = new Dictionary<string, string>
        {
            [new string('n', 257)] = "Length of message attribute name must be less than 256 bytes.",
            [".lead"] = "Invalid message attribute name starting with character '.' was found.",
            ["trail."] = "Invalid message attribute name ending with character '.' was found.",
            ["a..b"] = "Message attribute name can not have successive '.' character.",
        };
        var observed = new Dictionary<string, (string? Code, string? Message)>();
        foreach (var name in refusals.Keys)
        {
            observed[name] = await Error(() => sns.PublishAsync(new PublishRequest
            {
                TopicArn = topic, Message = $"refused-{Head(name, 12)}", MessageAttributes = Attr(name),
            }));
        }

        var wrong = observed.Where(kv => kv.Value != ("ParameterValueInvalid", refusals[kv.Key]))
            .ToDictionary(kv => Head(kv.Key, 20), kv => kv.Value);
        Ensure(wrong.Count == 0,
            $"attribute-name refusals differ from the rules: {{{string.Join(", ", wrong.Select(kv => $"'{kv.Key}': ('{kv.Value.Code}', '{kv.Value.Message}')"))}}}");

        var okBody = FidelityContext.UniqueName("sim2real-attrname-ok-");
        var mid = (await sns.PublishAsync(new PublishRequest
        {
            TopicArn = topic,
            Message = okBody,
            MessageAttributes = new()
            {
                [longOk] = new SnsMessageAttributeValue { DataType = "String", StringValue = "long" },
                ["a.b.c"] = new SnsMessageAttributeValue { DataType = "String", StringValue = "dotted" },
            },
        })).MessageId;

        var envs = new List<JsonObject>();
        var clock = FidelityTime.StartNew();
        var deadline = TimeSpan.FromSeconds(90);
        TimeSpan? tail = null;
        while (clock.Elapsed < deadline && (tail is null || clock.Elapsed < tail))
        {
            if (tail is null && envs.Any(e => (string?)e["MessageId"] == mid))
            {
                // Keep receiving briefly after the accepted message so a late refused one shows up.
                tail = clock.Elapsed + TimeSpan.FromSeconds(6);
            }

            foreach (var m in (await sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = url, MaxNumberOfMessages = 10, WaitTimeSeconds = 2 })).Messages ?? [])
            {
                envs.Add(JsonNode.Parse(m.Body)!.AsObject());
                await sqs.DeleteMessageAsync(new DeleteMessageRequest { QueueUrl = url, ReceiptHandle = m.ReceiptHandle });
            }
        }

        Ensure(envs.Select(e => (string?)e["MessageId"]).SequenceEqual([mid]),
            $"the queue must hold only the accepted message; it received {envs.Count} message(s), " +
            $"bodies [{string.Join(", ", envs.Select(e => $"'{Head(e["Message"] is JsonValue v ? v.ToString() : e["Message"]?.ToJsonString() ?? "None", 20)}'"))}]");
        var wantAttrs = new JsonObject
        {
            [longOk] = new JsonObject { ["Type"] = "String", ["Value"] = "long" },
            ["a.b.c"] = new JsonObject { ["Type"] = "String", ["Value"] = "dotted" },
        };
        Ensure(JsonNode.DeepEquals(envs[0]["MessageAttributes"], wantAttrs),
            "the delivered attributes are " +
            $"[{string.Join(", ", (envs[0]["MessageAttributes"] as JsonObject ?? []).Select(kv => (Head(kv.Key, 20), kv.Key.Length)).Order().Select(p => $"('{p.Item1}', {p.Length})"))}]");
    }

    private static string Head(string s, int n) => s.Length <= n ? s : s[..n];

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
}
