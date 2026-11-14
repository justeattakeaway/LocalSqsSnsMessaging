// Translated from behavioral/sqs/check_message_attributes_round_trip_types.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Diagnostics;
using Amazon.SQS;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sqs;

/// <summary>sqs.message-attributes-round-trip-types</summary>
[Category("AwsEvaluationSuite")]
public sealed class MessageAttributesRoundTripTypes
{
    private const string Check = "message-attributes-round-trip-types";

    /// <summary>
    /// String, Number, and Binary MessageAttributes survive a SendMessage/ReceiveMessage
    /// round trip with their DataType and value intact.
    /// </summary>
    [Test]
    [DisplayName("sqs.message-attributes-round-trip-types")]
    [Timeout(FidelityContext.BudgetMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sqs = ctx.Sqs;
        var url = (await sqs.CreateQueueAsync(new CreateQueueRequest
        {
            QueueName = FidelityContext.UniqueName("sim2real-sqs-msgattr-"),
            Tags = new() { ["sim2real"] = Check },
        })).QueueUrl;
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(url));

        var attrs = new Dictionary<string, MessageAttributeValue>
        {
            ["StringAttr"] = new() { DataType = "String", StringValue = "hello-world" },
            ["NumberAttr"] = new() { DataType = "Number", StringValue = "42" },
            ["BinaryAttr"] = new() { DataType = "Binary", BinaryValue = new MemoryStream([0x00, 0x01, 0xff, 0xfe]) },
        };
        await sqs.SendMessageAsync(new SendMessageRequest { QueueUrl = url, MessageBody = "attrs-round-trip", MessageAttributes = attrs });

        var msg = await PollReceiveOne(sqs, url, timeoutSeconds: 15.0);
        Ensure(msg.Body == "attrs-round-trip", $"expected body 'attrs-round-trip', got '{msg.Body}'");
        var received = msg.MessageAttributes!;

        Ensure(received["StringAttr"].DataType == "String",
            $"expected StringAttr DataType 'String', got '{received["StringAttr"].DataType}'");
        Ensure(received["StringAttr"].StringValue == "hello-world",
            $"expected StringAttr value 'hello-world', got '{received["StringAttr"].StringValue}'");

        Ensure(received["NumberAttr"].DataType == "Number",
            $"expected NumberAttr DataType 'Number', got '{received["NumberAttr"].DataType}'");
        Ensure(received["NumberAttr"].StringValue == "42",
            $"expected NumberAttr value '42', got '{received["NumberAttr"].StringValue}'");

        Ensure(received["BinaryAttr"].DataType == "Binary",
            $"expected BinaryAttr DataType 'Binary', got '{received["BinaryAttr"].DataType}'");
        var gotBinary = received["BinaryAttr"].BinaryValue!.ToArray();
        Ensure(gotBinary.AsSpan().SequenceEqual(new byte[] { 0x00, 0x01, 0xff, 0xfe }),
            $"expected BinaryAttr value b'\\x00\\x01\\xff\\xfe', got {Convert.ToHexStringLower(gotBinary)}");

        await sqs.DeleteMessageAsync(new DeleteMessageRequest { QueueUrl = url, ReceiptHandle = msg.ReceiptHandle });
    }

    private static async Task<Message> PollReceiveOne(AmazonSQSClient sqs, string url, double timeoutSeconds = 15.0)
    {
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(timeoutSeconds))
        {
            var resp = await sqs.ReceiveMessageAsync(new ReceiveMessageRequest
            {
                QueueUrl = url,
                MaxNumberOfMessages = 1,
                WaitTimeSeconds = 3,
                MessageAttributeNames = ["All"],
            });
            if (resp.Messages is { Count: > 0 } messages)
            {
                return messages[0];
            }
        }

        throw Fail($"no message received within {timeoutSeconds}s");
    }
}
