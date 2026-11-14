// Translated from behavioral/sns/check_message_attributes_survive_delivery_byte_for_byte.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Text.Json.Nodes;
using Amazon.SimpleNotificationService.Model;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sns;

/// <summary>sns.message-attributes-survive-delivery-byte-for-byte</summary>
[Category("AwsEvaluationSuite")]
public sealed class MessageAttributesSurviveDeliveryByteForByte
{
    private const string Scenario = "sns-message-attributes";

    // Every byte value, including NUL and the high half - the payload a UTF-8 round trip would corrupt.
    private static readonly byte[] Binary = Enumerable.Range(0, 256).Select(i => (byte)i).ToArray();

    // SNS rejects a non-BMP codepoint in a String attribute despite the documented range, so this is
    // the widest value it accepts: multi-byte BMP scripts, a combining mark and a Devanagari cluster.
    private const string Unicode = "sim2real é 日本語 क्ष";

    /// <summary>
    /// Binary, String and Number message attributes published to a topic arrive inside the SQS
    /// notification envelope with their exact types and values, including a binary value carrying every
    /// byte from 0x00 to 0xff.
    /// </summary>
    [Test]
    [DisplayName("sns.message-attributes-survive-delivery-byte-for-byte")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sns = ctx.Sns;
        var topicArn = await CreateTopic(ctx);
        var (queueUrl, queueArn) = await CreateQueueForTopic(ctx, topicArn);
        await SubscribeAndConfirm(ctx, topicArn, queueArn);

        var marker = FidelityContext.UniqueName("sim2real-behav-sns-attrs-");
        using var binaryStream = new MemoryStream(Binary);
        await sns.PublishAsync(new PublishRequest
        {
            TopicArn = topicArn,
            Message = marker,
            MessageAttributes = new()
            {
                ["bin"] = new() { DataType = "Binary", BinaryValue = binaryStream },
                ["s"] = new() { DataType = "String", StringValue = Unicode },
                ["n"] = new() { DataType = "Number", StringValue = "1.500" },
            },
        });

        var envelope = JsonNode.Parse(await AwaitBody(ctx, queueUrl, marker))!.AsObject();
        var attrs = envelope["MessageAttributes"]!.AsObject();

        var names = attrs.Select(kv => kv.Key).ToHashSet();
        Ensure(names.SetEquals(["bin", "s", "n"]),
            $"the delivered attribute set is wrong: [{string.Join(", ", names.Order(StringComparer.Ordinal).Select(n => $"'{n}'"))}]");
        // The envelope carries a Binary attribute base64-encoded.
        var decodedBin = Convert.FromBase64String((string)attrs["bin"]!["Value"]!);
        int? firstDiff = null;
        for (var i = 0; i < Math.Min(decodedBin.Length, Binary.Length); i++)
        {
            if (decodedBin[i] != Binary[i])
            {
                firstDiff = i;
                break;
            }
        }

        Ensure(decodedBin.AsSpan().SequenceEqual(Binary),
            $"the binary attribute was corrupted in delivery: {decodedBin.Length} bytes back of " +
            $"{Binary.Length}, first differing index {(firstDiff?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "None")}, " +
            $"head {Convert.ToHexString(decodedBin.AsSpan(0, Math.Min(16, decodedBin.Length)))}");
        Ensure((string?)attrs["bin"]!["Type"] == "Binary", $"the binary attribute lost its type: {attrs["bin"]!.ToJsonString()}");
        Ensure((string?)attrs["s"]!["Value"] == Unicode, $"the string attribute was mangled: '{attrs["s"]!["Value"]}' != '{Unicode}'");
        Ensure((string?)attrs["n"]!["Type"] == "Number" && (string?)attrs["n"]!["Value"] == "1.500",
            $"the number attribute lost its representation: {attrs["n"]!.ToJsonString()}");
    }

    private static string Head(string s, int n) => s.Length <= n ? s : s[..n];

    private static List<Amazon.SimpleNotificationService.Model.Tag> TagsKv() =>
        [new() { Key = "sim2real", Value = Scenario }, new() { Key = "sim2real:managed", Value = "true" }];

    /// <summary>CreateTopic (tagged + prefix-named) and register DeleteTopic immediately; returns the ARN.</summary>
    private static async Task<string> CreateTopic(FidelityContext ctx, bool fifo = false, Dictionary<string, string>? attributes = null)
    {
        var sns = ctx.Sns;
        var name = FidelityContext.UniqueName("sim2real-behav-sns-t-");
        var attrs = new Dictionary<string, string>(attributes ?? []);
        if (fifo)
        {
            name += ".fifo";
            attrs["FifoTopic"] = "true";
        }

        var arn = (await sns.CreateTopicAsync(new CreateTopicRequest { Name = name, Attributes = attrs, Tags = TagsKv() })).TopicArn;
        ctx.OnCleanup(() => ctx.Sns.DeleteTopicAsync(new DeleteTopicRequest { TopicArn = arn }));
        return arn;
    }

    /// <summary>Create a real SQS queue whose policy lets exactly this topic publish to it.</summary>
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
        ctx.OnCleanup(() => ctx.Sqs.DeleteQueueAsync(url));
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
        var deadline = FidelityTime.StartNew();
        string? pending = null;
        while (deadline.Elapsed < TimeSpan.FromSeconds(timeoutS))
        {
            pending = (await sns.GetSubscriptionAttributesAsync(new GetSubscriptionAttributesRequest { SubscriptionArn = subArn }))
                .Attributes?.GetValueOrDefault("PendingConfirmation");
            if (pending == "false")
            {
                return subArn;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(1.0));
        }

        throw Fail($"subscription '{subArn}' was still PendingConfirmation='{pending}' after {timeoutS}s");
    }

    /// <summary>Long-poll the queue until a body containing needle arrives; returns that body.</summary>
    private static async Task<string> AwaitBody(FidelityContext ctx, string queueUrl, string needle, double timeoutS = 90.0)
    {
        var sqs = ctx.Sqs;
        var seen = new List<string>();
        var deadline = FidelityTime.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(timeoutS))
        {
            var resp = await sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = queueUrl, MaxNumberOfMessages = 10, WaitTimeSeconds = 5 });
            foreach (var msg in resp.Messages ?? [])
            {
                await sqs.DeleteMessageAsync(new DeleteMessageRequest { QueueUrl = queueUrl, ReceiptHandle = msg.ReceiptHandle });
                var body = msg.Body ?? "";
                seen.Add(body);
                if (body.Contains(needle, StringComparison.Ordinal))
                {
                    return body;
                }
            }
        }

        throw Fail($"no message containing '{needle}' was delivered within {timeoutS}s; " +
            $"bodies seen: [{string.Join(", ", seen.Select(b => $"'{Head(b, 120)}'"))}]");
    }
}
