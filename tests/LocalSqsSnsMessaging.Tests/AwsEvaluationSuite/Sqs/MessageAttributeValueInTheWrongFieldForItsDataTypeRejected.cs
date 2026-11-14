// Translated from behavioral/sqs/check_message_attribute_value_in_the_wrong_field_for_its_data_type_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Diagnostics;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sqs;

/// <summary>sqs.message-attribute-value-in-the-wrong-field-for-its-data-type-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class MessageAttributeValueInTheWrongFieldForItsDataTypeRejected
{
    private const string Scenario = "sqs-attribute-value-field";

    /// <summary>
    /// SendMessage refuses an attribute whose value is in the wrong field for its DataType, or has
    /// no value, with the exact message; in SendMessageBatch such entries fail alone (SenderFault) and
    /// only well-formed messages are delivered, attributes intact.
    /// </summary>
    [Test]
    [DisplayName("sqs.message-attribute-value-in-the-wrong-field-for-its-data-type-rejected")]
    [Timeout(FidelityContext.BudgetMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sqs = ctx.Sqs;
        var url = await CreateQueue(ctx);

        // Each value is a factory: an SDK MessageAttributeValue carries a MemoryStream, so every
        // request gets its own instance, as each botocore call serializes the dict afresh.
        (string Name, Func<MessageAttributeValue> Value, string[] Fields, string Expected)[] cases =
        [
            ("blob", () => new() { DataType = "Binary", StringValue = "aGk=" }, ["DataType", "StringValue"],
                "Message (user) attribute 'blob' with type 'Binary' must use field 'Binary'."),
            ("label", () => new() { DataType = "String", BinaryValue = new MemoryStream("hi"u8.ToArray()) }, ["BinaryValue", "DataType"],
                "Message (user) attribute 'label' with type 'String' must use field 'String'."),
            ("count", () => new() { DataType = "Number", BinaryValue = new MemoryStream("1"u8.ToArray()) }, ["BinaryValue", "DataType"],
                "Message (user) attribute 'count' with type 'Number' must use field 'String'."),
            ("label", () => new() { DataType = "String" }, ["DataType"],
                "Message (user) attribute 'label' must contain a non-empty value of type 'String'."),
        ];
        for (var i = 0; i < cases.Length; i++)
        {
            var (name, value, fields, expected) = cases[i];
            var b = $"sim2real-refused-{i}";
            var (code, message) = await Error(() => sqs.SendMessageAsync(new SendMessageRequest
            {
                QueueUrl = url, MessageBody = b, MessageAttributes = new() { [name] = value() },
            }));
            Ensure((code, message) == ("InvalidParameterValue", expected),
                $"attribute '{name}' with fields [{string.Join(", ", fields)}]: got '{code}' '{message}'");
        }

        var mixed = await sqs.SendMessageBatchAsync(new SendMessageBatchRequest
        {
            QueueUrl = url,
            Entries =
            [
                new SendMessageBatchRequestEntry
                {
                    Id = "good", MessageBody = "sim2real-accepted",
                    MessageAttributes = new() { ["blob"] = new MessageAttributeValue { DataType = "Binary", BinaryValue = new MemoryStream([0x00, 0xff]) } },
                },
                new SendMessageBatchRequestEntry
                {
                    Id = "bad", MessageBody = "sim2real-refused-mixed",
                    MessageAttributes = new() { ["blob"] = new MessageAttributeValue { DataType = "Binary", StringValue = "aGk=" } },
                },
            ],
        });
        Ensure((mixed.Successful ?? []).Select(e => e.Id).SequenceEqual(["good"]),
            $"only the well-formed entry may succeed, got [{string.Join(", ", (mixed.Successful ?? []).Select(e => e.Id))}]");
        var failed = (mixed.Failed ?? []).Select(e => (
            e.Id,
            e.Code ?? throw new KeyNotFoundException("Code"),
            e.Message ?? throw new KeyNotFoundException("Message"),
            e.SenderFault ?? throw new KeyNotFoundException("SenderFault"))).ToList();
        Ensure(failed.SequenceEqual([("bad", "InvalidParameterValue", cases[0].Expected, true)]),
            $"the malformed entry must fail alone, got [{string.Join(", ", failed)}]");

        var allBad = await sqs.SendMessageBatchAsync(new SendMessageBatchRequest
        {
            QueueUrl = url,
            Entries = cases[..2].Select((c, i) => new SendMessageBatchRequestEntry
            {
                Id = $"bad{i}", MessageBody = $"sim2real-refused-all-{i}", MessageAttributes = new() { [c.Name] = c.Value() },
            }).ToList(),
        });
        Ensure(allBad.Successful is null || allBad.Successful.Count == 0,
            $"a batch of only malformed entries must report no success, got [{string.Join(", ", (allBad.Successful ?? []).Select(e => e.Id))}]");
        var failedAll = (allBad.Failed ?? []).Select(e => (
            e.Id,
            e.Code ?? throw new KeyNotFoundException("Code"),
            e.Message ?? throw new KeyNotFoundException("Message"))).Order().ToList();
        Ensure(failedAll.SequenceEqual([("bad0", "InvalidParameterValue", cases[0].Expected),
                    ("bad1", "InvalidParameterValue", cases[1].Expected)]),
            $"every malformed entry must be listed as Failed, got [{string.Join(", ", failedAll)}]");

        var seen = await ReceiveAll(ctx, url, ["sim2real-accepted"], messageAttributeNames: ["All"]);
        foreach (var (body, msg) in await ReceiveWindow(ctx, url, 5.0))
        {
            seen[body] = msg;
        }

        var blob = (seen["sim2real-accepted"].MessageAttributes ?? []).GetValueOrDefault("blob");
        var blobBinary = blob?.BinaryValue?.ToArray();
        Ensure(blob?.DataType == "Binary" && blobBinary is not null && blobBinary.AsSpan().SequenceEqual(new byte[] { 0x00, 0xff }),
            $"the accepted entry's Binary attribute arrived as {{DataType: {blob?.DataType}, BinaryValue: {(blobBinary is null ? "None" : Convert.ToHexString(blobBinary))}}}");
        Ensure(seen.Keys.ToHashSet().SetEquals(["sim2real-accepted"]),
            $"refused messages were enqueued: [{string.Join(", ", seen.Keys.Where(k => k != "sim2real-accepted").Order(StringComparer.Ordinal))}]");
    }

    /// <summary>The (Code, Message) of the ClientError ``fn`` raises. An unexpected success raises too.</summary>
    private static async Task<(string Code, string Message)> Error<T>(Func<Task<T>> fn)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is null)
        {
            throw Fail($"expected the call to be refused, but it succeeded: {result}");
        }

        return (error.Code ?? throw new KeyNotFoundException("Code"), error.Message ?? "");
    }

    /// <summary>CreateQueue (tagged + prefix-named) and register DeleteQueue immediately.</summary>
    private static async Task<string> CreateQueue(FidelityContext ctx, Dictionary<string, string>? attributes = null)
    {
        var sqs = ctx.Sqs;
        var url = (await sqs.CreateQueueAsync(new CreateQueueRequest
        {
            QueueName = FidelityContext.UniqueName("sim2real-behav-sqs-"), Attributes = attributes ?? [],
            Tags = new() { ["sim2real"] = Scenario, ["sim2real:managed"] = "true" },
        })).QueueUrl;
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(url));
        return url;
    }

    /// <summary>
    /// Receive (and delete) until every body in ``wanted`` has been seen; body -> message for
    /// EVERY message seen on the way.
    /// </summary>
    private static async Task<Dictionary<string, Message>> ReceiveAll(FidelityContext ctx, string url, HashSet<string> wanted, double timeoutS = 60.0, List<string>? messageAttributeNames = null)
    {
        var sqs = ctx.Sqs;
        var seen = new Dictionary<string, Message>();
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed.TotalSeconds < timeoutS && !wanted.IsSubsetOf(seen.Keys))
        {
            var request = new ReceiveMessageRequest { QueueUrl = url, MaxNumberOfMessages = 10, WaitTimeSeconds = 2 };
            if (messageAttributeNames is not null)
            {
                request.MessageAttributeNames = messageAttributeNames;
            }

            foreach (var msg in (await sqs.ReceiveMessageAsync(request)).Messages ?? [])
            {
                await sqs.DeleteMessageAsync(new DeleteMessageRequest { QueueUrl = url, ReceiptHandle = msg.ReceiptHandle });
                seen[msg.Body] = msg;
            }
        }

        var missing = wanted.Except(seen.Keys).Order(StringComparer.Ordinal).ToList();
        Ensure(missing.Count == 0,
            $"within {timeoutS}s the queue never delivered [{string.Join(", ", missing)}]; it delivered [{string.Join(", ", seen.Keys.Order(StringComparer.Ordinal))}]");
        return seen;
    }

    /// <summary>Receive (and delete) everything delivered within ``window_s``; body -> message.</summary>
    private static async Task<Dictionary<string, Message>> ReceiveWindow(FidelityContext ctx, string url, double windowS)
    {
        var sqs = ctx.Sqs;
        var seen = new Dictionary<string, Message>();
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed.TotalSeconds < windowS)
        {
            foreach (var msg in (await sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = url, MaxNumberOfMessages = 10, WaitTimeSeconds = 1 })).Messages ?? [])
            {
                await sqs.DeleteMessageAsync(new DeleteMessageRequest { QueueUrl = url, ReceiptHandle = msg.ReceiptHandle });
                seen[msg.Body] = msg;
            }
        }

        return seen;
    }
}
