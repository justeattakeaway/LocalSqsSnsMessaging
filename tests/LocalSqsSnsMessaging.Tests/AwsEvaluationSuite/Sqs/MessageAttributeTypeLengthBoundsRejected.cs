// Translated from behavioral/sqs/check_message_attribute_type_length_bounds_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Diagnostics;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sqs;

/// <summary>sqs.message-attribute-type-length-bounds-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class MessageAttributeTypeLengthBoundsRejected
{
    private const string Check = "message-attribute-type-length-bounds-rejected";

    /// <summary>
    /// SendMessage refuses a message attribute DataType of 257 bytes with its exact message and
    /// enqueues nothing; a 256-byte DataType with the same valid 'String' prefix is accepted and
    /// round-trips unchanged.
    /// </summary>
    [Test]
    [DisplayName("sqs.message-attribute-type-length-bounds-rejected")]
    [Timeout(FidelityContext.BudgetMilliseconds)]
    [KnownFidelityGap(FidelityGap.Validation, "expected the call to be refused, but it succeeded: SendMessage with a 257-byte attribute DataType")]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sqs = ctx.Sqs;
        var url = (await sqs.CreateQueueAsync(new CreateQueueRequest
        {
            QueueName = FidelityContext.UniqueName("sim2real-behav-attrtypelen-"),
            Tags = new() { ["sim2real-check"] = Check },
        })).QueueUrl;
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(url));

        var over = "String." + new string('s', 250);  // 257 bytes
        Ensure(over.Length == 257, $"the over-limit probe must be 257 bytes, got {over.Length}");
        var (code, message) = await Error(() => sqs.SendMessageAsync(new SendMessageRequest
        {
            QueueUrl = url, MessageBody = "sim2real-refused",
            MessageAttributes = new() { ["N"] = new MessageAttributeValue { DataType = over, StringValue = "v" } },
        }));
        Ensure(code == "InvalidParameterValue", $"257-byte DataType: expected InvalidParameterValue, got '{code}'");
        Ensure(message == "Message (user) attribute type must be shorter than 256 bytes.",
            $"257-byte DataType: unexpected message '{message}'");

        var atBound = "String." + new string('s', 249);  // 256 bytes
        Ensure(atBound.Length == 256, $"the boundary probe must be 256 bytes, got {atBound.Length}");
        await sqs.SendMessageAsync(new SendMessageRequest
        {
            QueueUrl = url, MessageBody = "sim2real-accepted",
            MessageAttributes = new() { ["N"] = new MessageAttributeValue { DataType = atBound, StringValue = "v" } },
        });

        var got = new Dictionary<string, MessageAttributeValue?>();
        var timeoutS = BudgetS(ctx, 30);
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed.TotalSeconds < timeoutS && !got.ContainsKey("sim2real-accepted"))
        {
            foreach (var msg in (await sqs.ReceiveMessageAsync(new ReceiveMessageRequest
            {
                QueueUrl = url, MaxNumberOfMessages = 10, WaitTimeSeconds = 2, MessageAttributeNames = ["All"],
            })).Messages ?? [])
            {
                got[msg.Body] = (msg.MessageAttributes ?? []).GetValueOrDefault("N");
                await sqs.DeleteMessageAsync(new DeleteMessageRequest { QueueUrl = url, ReceiptHandle = msg.ReceiptHandle });
            }
        }

        Ensure(!got.ContainsKey("sim2real-refused"),
            "the refused 257-byte DataType must not have been enqueued, but it was received: " +
            $"{got.GetValueOrDefault("sim2real-refused")?.DataType}");
        if (!got.ContainsKey("sim2real-accepted"))
        {
            throw new TimeoutException($"the accepted message was not delivered within {timeoutS:F0}s");
        }

        var accepted = got["sim2real-accepted"];
        Ensure(accepted is not null && accepted.DataType == atBound,
            $"the 256-byte DataType must round-trip unchanged, got DataType '{accepted?.DataType}'");
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

    /// <summary>A wait's timeout, bounded by the runner's remaining time so the wait fails with its own message.</summary>
    private static double BudgetS(FidelityContext ctx, double capS)
    {
        var left = ctx.TimeLeftSeconds;
        return Math.Max(1.0, Math.Min(capS, left - 5.0));
    }
}
