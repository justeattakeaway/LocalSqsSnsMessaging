// Translated from behavioral/sqs/check_default_max_message_size_is_one_mib_boundary.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Amazon.SQS;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sqs;

/// <summary>sqs.default-max-message-size-is-one-mib-boundary</summary>
[Category("AwsEvaluationSuite")]
public sealed class DefaultMaxMessageSizeIsOneMibBoundary
{
    private const int OneMib = 1048576;
    private const string OversizeMessage =
        "One or more parameters are invalid. Reason: Message must be shorter than 1048576 bytes.";

    /// <summary>
    /// A queue created with no attributes reports MaximumMessageSize 1 MiB, accepts a 1 MiB body that
    /// comes back byte-exact, and refuses 1 MiB + 1, which is then absent from the queue.
    /// </summary>
    [Test]
    [DisplayName("sqs.default-max-message-size-is-one-mib-boundary")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sqs = ctx.Sqs;
        var url = (await sqs.CreateQueueAsync(new CreateQueueRequest { QueueName = FidelityContext.UniqueName("sim2real-behav-1mib-") })).QueueUrl;
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(url));

        var @default = (await sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest { QueueUrl = url, AttributeNames = ["MaximumMessageSize"] }))
            .Attributes!["MaximumMessageSize"];
        Ensure(@default == OneMib.ToString(),
            $"a queue created with no attributes reports MaximumMessageSize={@default}, expected " +
            $"{OneMib} (the 1 MiB default)");

        var atLimit = new string('a', OneMib);
        var sent = await sqs.SendMessageAsync(new SendMessageRequest { QueueUrl = url, MessageBody = atLimit });
        var wantMd5 = Convert.ToHexStringLower(MD5.HashData(Encoding.UTF8.GetBytes(atLimit)));
        Ensure(sent.MD5OfMessageBody == wantMd5,
            $"MD5OfMessageBody for the 1 MiB body was {sent.MD5OfMessageBody}, computed {wantMd5}");

        var err = await Error(() => sqs.SendMessageAsync(new SendMessageRequest { QueueUrl = url, MessageBody = new string('b', OneMib + 1) }));
        Ensure(err.Code == "InvalidParameterValue",
            $"a {OneMib + 1}-byte body gave code {err.Code}, expected 'InvalidParameterValue'");
        Ensure(err.Message == OversizeMessage,
            $"expected message {OversizeMessage}, got {err.Message}");

        var got = await Drain(sqs, url, timeoutSeconds: 30);
        var lengths = got.Select(m => m.Body.Length).Order().ToList();
        Ensure(lengths.SequenceEqual([OneMib]),
            $"expected exactly the one {OneMib}-byte message on the queue, received bodies of " +
            $"lengths [{string.Join(", ", lengths)}]");
        var body = got[0].Body;
        Ensure(body == atLimit,
            "the 1 MiB body did not round-trip byte-exact: first differing prefix " +
            $"{body[..Math.Min(16, body.Length)]}..., length {body.Length}");
        Ensure(got[0].MD5OfBody == wantMd5,
            $"MD5OfBody on receive was {got[0].MD5OfBody}, computed {wantMd5}");
    }

    /// <summary>
    /// The botocore Error dict <paramref name="fn"/> raises. An unexpected success raises too, naming what came
    /// back: a target that accepts a body real SQS refuses must not read as a code mismatch.
    /// </summary>
    private static async Task<AwsError> Error<T>(Func<Task<T>> fn)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is not null)
        {
            return error;
        }

        throw Fail($"expected the call to be refused, but it succeeded: {Repr(result)}");
    }

    /// <summary>Every message receivable within the bounded window, each deleted once read.</summary>
    private static async Task<List<Message>> Drain(AmazonSQSClient sqs, string url, double timeoutSeconds)
    {
        var got = new List<Message>();
        var deadline = FidelityTime.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(timeoutSeconds))
        {
            var msgs = (await sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = url, MaxNumberOfMessages = 10, WaitTimeSeconds = 2 })).Messages ?? [];
            if (msgs.Count == 0 && got.Count > 0)
            {
                break;
            }

            foreach (var m in msgs)
            {
                got.Add(m);
                await sqs.DeleteMessageAsync(new DeleteMessageRequest { QueueUrl = url, ReceiptHandle = m.ReceiptHandle });
            }
        }

        return got;
    }

    private static string Repr(object? value)
    {
        try
        {
            var text = JsonSerializer.Serialize(value, value?.GetType() ?? typeof(object));
            return text.Length > 300 ? text[..300] + "..." : text;
        }
        catch (Exception)
        {
            return value?.ToString() ?? "None";
        }
    }
}
