// Translated from behavioral/sqs/check_dead_lettered_message_keeps_its_identity_and_names_its_source_queue.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Diagnostics;
using System.Text.Json;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sqs;

/// <summary>sqs.dead-lettered-message-keeps-its-identity-and-names-its-source-queue</summary>
[Category("AwsEvaluationSuite")]
public sealed class DeadLetteredMessageKeepsItsIdentityAndNamesItsSourceQueue
{
    private const string Scenario = "sqs-dlq-message-identity";

    private const string Trace = "Root=1-5759e988-bd862e3fe1be46a994272793;Parent=53995c3f42cd8ad8;Sampled=1";

    /// <summary>
    /// A dead-lettered message keeps its MessageId, body, MD5s, MessageAttributes and AWSTraceHeader,
    /// gains DeadLetterQueueSourceArn naming the source, and leaves the source; a RedrivePolicy with
    /// maxReceiveCount "1" reads back as compact JSON with the number 1.
    /// </summary>
    [Test]
    [DisplayName("sqs.dead-lettered-message-keeps-its-identity-and-names-its-source-queue")]
    [Timeout(FidelityContext.BudgetMilliseconds)]
    [KnownFidelityGap(FidelityGap.DeadLetterQueues, "RedrivePolicy must read back as compact JSON with a numeric maxReceiveCount, but it reads back as sent (spaced, count \"1\")")]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sqs = ctx.Sqs;
        var dlq = await CreateQueue(ctx);
        var dlqArn = (await sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest { QueueUrl = dlq, AttributeNames = ["QueueArn"] })).Attributes!["QueueArn"];
        var src = await CreateQueue(ctx, new() { ["VisibilityTimeout"] = "1" });
        var srcArn = (await sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest { QueueUrl = src, AttributeNames = ["QueueArn"] })).Attributes!["QueueArn"];

        await sqs.SetQueueAttributesAsync(new SetQueueAttributesRequest
        {
            QueueUrl = src,
            Attributes = new() { ["RedrivePolicy"] = PyDumps(("deadLetterTargetArn", dlqArn), ("maxReceiveCount", "1")) },
        });
        var stored = (await sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest { QueueUrl = src, AttributeNames = ["RedrivePolicy"] })).Attributes!;
        // json.dumps(..., separators=(",", ":"))
        var expectedPolicy = $"{{\"deadLetterTargetArn\":{JsonSerializer.Serialize(dlqArn)},\"maxReceiveCount\":1}}";
        Ensure(stored.GetValueOrDefault("RedrivePolicy") == expectedPolicy,
            "RedrivePolicy must read back as compact JSON with a numeric count, got " +
            $"{Last(stored.GetValueOrDefault("RedrivePolicy") ?? "", 40)}");

        var attrs = new Dictionary<string, MessageAttributeValue>
        {
            ["tenant"] = new() { DataType = "String", StringValue = "acme" },
            ["attempt"] = new() { DataType = "Number", StringValue = "7" },
            ["blob"] = new() { DataType = "Binary", BinaryValue = new MemoryStream([0x00, 0x01, 0xfe]) },
        };
        var sent = await sqs.SendMessageAsync(new SendMessageRequest
        {
            QueueUrl = src,
            MessageBody = "sim2real-poison",
            MessageAttributes = attrs,
            MessageSystemAttributes = new()
            {
                ["AWSTraceHeader"] = new() { DataType = "String", StringValue = Trace },
            },
        });

        Message? moved = null;
        var srcDeliveries = 0;
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(90.0) && moved is null)
        {
            srcDeliveries += ((await sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = src, WaitTimeSeconds = 2 })).Messages ?? []).Count;
            var got = (await sqs.ReceiveMessageAsync(new ReceiveMessageRequest
            {
                QueueUrl = dlq,
                WaitTimeSeconds = 1,
                MessageAttributeNames = ["All"],
                MessageSystemAttributeNames = ["All"],
            })).Messages ?? [];
            moved = got.Count > 0 ? got[0] : null;
        }

        Ensure(moved is not null,
            $"the message never reached the DLQ within 90s ({srcDeliveries} source deliveries)");
        Ensure(srcDeliveries >= 1, "the message reached the DLQ without ever being delivered");

        Ensure((moved.MessageId, moved.Body, moved.MD5OfBody) == (sent.MessageId, "sim2real-poison", sent.MD5OfMessageBody),
            "the dead-lettered message changed identity: MessageId matches=" +
            $"{moved.MessageId == sent.MessageId}, body={moved.Body}");
        var gotAttrs = (moved.MessageAttributes ?? []).ToDictionary(
            kv => kv.Key,
            kv => (kv.Value.DataType, (object?)(string.IsNullOrEmpty(kv.Value.StringValue) ? kv.Value.BinaryValue?.ToArray() : kv.Value.StringValue)));
        Ensure(gotAttrs.Count == 3
               && gotAttrs.TryGetValue("tenant", out var tenant) && tenant.DataType == "String" && tenant.Item2 is "acme"
               && gotAttrs.TryGetValue("attempt", out var attempt) && attempt.DataType == "Number" && attempt.Item2 is "7"
               && gotAttrs.TryGetValue("blob", out var blob) && blob.DataType == "Binary" && blob.Item2 is byte[] bytes && bytes.SequenceEqual(new byte[] { 0x00, 0x01, 0xfe }),
            $"MessageAttributes changed on the move: {string.Join(", ", gotAttrs.Select(kv => $"{kv.Key}=({kv.Value.DataType}, {kv.Value.Item2})"))}");
        Ensure(moved.MD5OfMessageAttributes == Key(sent.MD5OfMessageAttributes, "MD5OfMessageAttributes"),
            "MD5OfMessageAttributes differs between send and the DLQ copy");
        var system = moved.Attributes ?? [];
        Ensure(system.GetValueOrDefault("AWSTraceHeader") == Trace,
            $"AWSTraceHeader was not carried to the DLQ: {system.GetValueOrDefault("AWSTraceHeader")}");
        Ensure(system.GetValueOrDefault("DeadLetterQueueSourceArn") == srcArn,
            "DeadLetterQueueSourceArn must name the source queue, it was " +
            $"{(!system.ContainsKey("DeadLetterQueueSourceArn") ? "absent" : "another ARN")}");

        await sqs.DeleteMessageAsync(new DeleteMessageRequest { QueueUrl = dlq, ReceiptHandle = moved.ReceiptHandle });
        var again = (await sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = src, WaitTimeSeconds = 3, VisibilityTimeout = 0 })).Messages ?? [];
        Ensure(again.Count == 0, $"the source still delivers the moved message: {string.Join(", ", again.Select(m => m.Body))}");
    }

    /// <summary>CreateQueue (tagged + prefix-named) and register DeleteQueue immediately.</summary>
    private static async Task<string> CreateQueue(FidelityContext ctx, Dictionary<string, string>? attributes = null)
    {
        var sqs = ctx.Sqs;
        var url = (await sqs.CreateQueueAsync(new CreateQueueRequest
        {
            QueueName = FidelityContext.UniqueName("sim2real-behav-sqs-"),
            Attributes = attributes ?? [],
            Tags = new() { ["sim2real"] = Scenario, ["sim2real:managed"] = "true" },
        })).QueueUrl;
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(url));
        return url;
    }

    /// <summary>Python's <c>resp["Key"]</c>: a <see cref="KeyNotFoundException"/> when the response omitted it.</summary>
    private static T Key<T>(T? value, string name)
        where T : class
        => value ?? throw new KeyNotFoundException(name);

    private static string Last(string value, int count) => value.Length <= count ? value : value[^count..];

    /// <summary>Python's <c>json.dumps</c> of a flat dict, with its default separators.</summary>
    private static string PyDumps(params (string Key, object Value)[] items)
        => "{" + string.Join(", ", items.Select(kv => $"{JsonSerializer.Serialize(kv.Key)}: {JsonSerializer.Serialize(kv.Value)}")) + "}";
}
