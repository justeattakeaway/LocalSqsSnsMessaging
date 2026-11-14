// Translated from behavioral/sqs/check_message_size_counts_attribute_name_type_and_value.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Diagnostics;
using System.Globalization;
using Amazon.SQS;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sqs;

/// <summary>sqs.message-size-counts-attribute-name-type-and-value</summary>
[Category("AwsEvaluationSuite")]
public sealed class MessageSizeCountsAttributeNameTypeAndValue
{
    private const int Limit = 1024;
    private const string OversizeMessage = "One or more parameters are invalid. Reason: Message must be shorter than 1024 bytes.";
    private const string TraceHeader = "Root=1-5759e988-bd862e3fe1be46a994272793;Parent=53995c3f42cd8ad8;Sampled=1";

    /// <summary>
    /// The botocore Error dict ``fn`` raises. An unexpected success raises too, naming what came
    /// back: a target that accepts an oversize message must not read as a code mismatch.
    /// </summary>
    private static async Task<AwsError> Error<T>(Func<Task<T>> fn)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        return error ?? throw Fail($"expected the call to be refused, but it succeeded: {Describe(result)}");
    }

    private static string Describe(object? result) => result switch
    {
        SendMessageResponse r => $"{{MD5OfMessageBody: {r.MD5OfMessageBody}, MessageId: {r.MessageId}}}",
        _ => result?.ToString() ?? "None",
    };

    /// <summary>
    /// Bodies receivable within the bounded window (deleted as read), stopping once ``want`` are
    /// in hand and a further receive comes back empty.
    /// </summary>
    private static async Task<List<string>> DrainBodies(AmazonSQSClient sqs, string url, int want, double timeoutSeconds)
    {
        var got = new List<string>();
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(timeoutSeconds))
        {
            var msgs = (await sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = url, MaxNumberOfMessages = 10, WaitTimeSeconds = 2 })).Messages ?? [];
            if (msgs.Count == 0 && got.Count >= want)
            {
                break;
            }

            foreach (var m in msgs)
            {
                got.Add(m.Body);
                await sqs.DeleteMessageAsync(new DeleteMessageRequest { QueueUrl = url, ReceiptHandle = m.ReceiptHandle });
            }
        }

        return got;
    }

    /// <summary>
    /// MaximumMessageSize counts the body plus each attribute's name, DataType (custom label in full)
    /// and value, but not AWSTraceHeader: exactly 1024 counted bytes is accepted, one more is
    /// refused, and only accepted messages are enqueued.
    /// </summary>
    [Test]
    [DisplayName("sqs.message-size-counts-attribute-name-type-and-value")]
    [Timeout(FidelityContext.BudgetMilliseconds)]
    [KnownFidelityGap(FidelityGap.Validation, "a message one byte over MaximumMessageSize once its attribute is counted was accepted instead of refused")]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sqs = ctx.Sqs;
        var url = (await sqs.CreateQueueAsync(new CreateQueueRequest
        {
            QueueName = FidelityContext.UniqueName("sim2real-behav-msgsize-"),
            Attributes = new() { ["MaximumMessageSize"] = Limit.ToString(CultureInfo.InvariantCulture) },
        })).QueueUrl;
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(url));

        var limit = (await sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest { QueueUrl = url, AttributeNames = ["MaximumMessageSize"] })).Attributes!["MaximumMessageSize"];
        Ensure(limit == Limit.ToString(CultureInfo.InvariantCulture), $"MaximumMessageSize read back as '{limit}', expected '{Limit}'");

        // 'Pad' + 'String' + 'ppp' = 12 bytes, so a 1012-byte body totals exactly 1024.
        static Dictionary<string, MessageAttributeValue> Pad() => new() { ["Pad"] = new() { DataType = "String", StringValue = "ppp" } };
        static Dictionary<string, MessageAttributeValue> Custom() => new() { ["Pad"] = new() { DataType = "String.ab", StringValue = "ppp" } };

        var accepted = new (string Label, string Body, Dictionary<string, MessageAttributeValue>? Attrs, Dictionary<string, MessageSystemAttributeValue>? SysAttrs)[]
        {
            ("body-only-at-limit", new string('c', 1024), null, null),
            ("string-attr-at-limit", new string('d', 1012), Pad(), null),
            ("custom-type-at-limit", new string('e', 1009), Custom(), null),
            ("trace-header-not-counted", new string('f', 1024), null,
                new() { ["AWSTraceHeader"] = new() { DataType = "String", StringValue = TraceHeader } }),
        };
        foreach (var (label, body, attrs, sysAttrs) in accepted)
        {
            var request = new SendMessageRequest { QueueUrl = url, MessageBody = body };
            if (attrs is not null)
            {
                request.MessageAttributes = attrs;
            }

            if (sysAttrs is not null)
            {
                request.MessageSystemAttributes = sysAttrs;
            }

            var error = await FidelityContext.CaptureError(() => sqs.SendMessageAsync(request));
            if (error is not null)
            {
                throw new FidelityAssertionException(
                    $"{label}: a message totalling exactly {Limit} counted bytes must be accepted, got " +
                    $"'{error.Code}': '{error.Message}'", error.Exception);
            }
        }

        var refused = new (string Label, string Body, Dictionary<string, MessageAttributeValue> Attrs)[]
        {
            ("string-attr-one-over", new string('g', 1013), Pad()),
            ("custom-type-one-over", new string('h', 1010), Custom()),
        };
        foreach (var (label, body, attrs) in refused)
        {
            var err = await Error(() => sqs.SendMessageAsync(new SendMessageRequest { QueueUrl = url, MessageBody = body, MessageAttributes = attrs }));
            Ensure(err.Code == "InvalidParameterValue",
                $"{label}: a message one byte over {Limit} once its attribute is counted gave code " +
                $"'{err.Code}', expected 'InvalidParameterValue'");
            Ensure(err.Message == OversizeMessage, $"{label}: expected message '{OversizeMessage}', got '{err.Message}'");
        }

        var bodies = await DrainBodies(sqs, url, want: accepted.Length, timeoutSeconds: 30);
        var got = bodies.Select(b => (b[0], b.Length)).Order().ToList();
        var want = accepted.Select(a => (a.Body[0], a.Body.Length)).Order().ToList();
        Ensure(got.SequenceEqual(want),
            $"the queue must hold exactly the four accepted messages and none of the refused ones; " +
            $"received (first char, length) [{string.Join(", ", got)}], expected [{string.Join(", ", want)}]");
    }
}
