// Translated from behavioral/sqs/check_send_message_malformed_awstraceheader_accepted_then_dropped.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.SQS;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sqs;

/// <summary>sqs.send-message-malformed-awstraceheader-accepted-then-dropped</summary>
[Category("AwsEvaluationSuite")]
public sealed class SendMessageMalformedAwstraceheaderAcceptedThenDropped
{
    private const string Check = "send-message-malformed-awstraceheader-accepted-then-dropped";

    private const string ValidTrace = "Root=1-5759e988-bd862e3fe1be46a994272793;Parent=53995c3f42cd8ad8;Sampled=1";

    private static Dictionary<string, MessageSystemAttributeValue> SilentlyDropped() => new()
    {
        ["garbage"] = new MessageSystemAttributeValue { DataType = "String", StringValue = "not-a-trace-header" },
        ["wrong-datatype-number"] = new MessageSystemAttributeValue { DataType = "Number", StringValue = "1" },
        ["wrong-datatype-binary"] = new MessageSystemAttributeValue { DataType = "Binary", BinaryValue = new MemoryStream("abc"u8.ToArray()) },
    };

    /// <summary>
    /// SendMessage refuses an empty AWSTraceHeader with its exact message; a message whose header is
    /// not DataType String or does not parse as a trace header is delivered without the header, and a
    /// well-formed header round-trips unchanged.
    /// </summary>
    [Test]
    [DisplayName("sqs.send-message-malformed-awstraceheader-accepted-then-dropped")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sqs = ctx.Sqs;
        var url = (await sqs.CreateQueueAsync(new CreateQueueRequest
        {
            QueueName = FidelityContext.UniqueName("sim2real-behav-traceheader-"),
            Tags = new() { ["sim2real-check"] = Check },
        })).QueueUrl;
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(url));

        var (code, message) = await Error(() => sqs.SendMessageAsync(new SendMessageRequest
        {
            QueueUrl = url,
            MessageBody = "sim2real-empty",
            MessageSystemAttributes = new() { ["AWSTraceHeader"] = new MessageSystemAttributeValue { DataType = "String", StringValue = "" } },
        }));
        Ensure(code == "InvalidParameterValue", $"empty AWSTraceHeader: expected InvalidParameterValue, got {code}");
        Ensure(message == "Message system attribute 'AWSTraceHeader' must contain a non-empty value of type 'String'.",
            $"empty AWSTraceHeader: unexpected message {message}");

        var bodiesSent = new HashSet<string>(StringComparer.Ordinal) { "sim2real-valid" };
        await sqs.SendMessageAsync(new SendMessageRequest
        {
            QueueUrl = url,
            MessageBody = "sim2real-valid",
            MessageSystemAttributes = new() { ["AWSTraceHeader"] = new MessageSystemAttributeValue { DataType = "String", StringValue = ValidTrace } },
        });
        foreach (var (label, sysattr) in SilentlyDropped())
        {
            var body = $"sim2real-{label}";
            bodiesSent.Add(body);
            await sqs.SendMessageAsync(new SendMessageRequest
            {
                QueueUrl = url,
                MessageBody = body,
                MessageSystemAttributes = new() { ["AWSTraceHeader"] = sysattr },
            });
        }

        var seen = await ReceiveAll(sqs, url, bodiesSent, timeoutSeconds: BudgetSeconds(ctx, 60.0));
        Ensure(seen.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(bodiesSent),
            $"the queue must deliver exactly the sent bodies; got [{string.Join(", ", seen.Keys.Order(StringComparer.Ordinal))}]");

        var gotValid = seen["sim2real-valid"].Attributes?.GetValueOrDefault("AWSTraceHeader");
        Ensure(gotValid == ValidTrace, $"the well-formed header must round-trip unchanged, got {gotValid}");
        foreach (var label in SilentlyDropped().Keys)
        {
            var got = seen[$"sim2real-{label}"].Attributes ?? [];
            Ensure(!got.ContainsKey("AWSTraceHeader"),
                $"{label}: the message must be delivered without the AWSTraceHeader, got {{{string.Join(", ", got.Select(kv => $"{kv.Key}: {kv.Value}"))}}}");
        }
    }

    /// <summary>The (Code, Message) of the ClientError <paramref name="fn"/> raises. An unexpected success raises too.</summary>
    private static async Task<(string? Code, string Message)> Error<T>(Func<Task<T>> fn)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is not null)
        {
            return (error.Code, error.Message ?? "");
        }

        throw Fail($"expected the call to be refused, but it succeeded: {Describe(result)}");
    }

    private static string Describe(object? result) => result switch
    {
        SendMessageResponse r => $"{{'MD5OfMessageBody': '{r.MD5OfMessageBody}', 'MessageId': '{r.MessageId}'}}",
        _ => result?.ToString() ?? "None",
    };

    /// <summary>Receive (and delete) until every body in <paramref name="wanted"/> has been seen; body -> message.</summary>
    private static async Task<Dictionary<string, Message>> ReceiveAll(AmazonSQSClient sqs, string url, HashSet<string> wanted, double timeoutSeconds)
    {
        var seen = new Dictionary<string, Message>(StringComparer.Ordinal);
        var deadline = FidelityTime.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(timeoutSeconds) && !wanted.IsSubsetOf(seen.Keys))
        {
            var resp = await sqs.ReceiveMessageAsync(new ReceiveMessageRequest
            {
                QueueUrl = url,
                MaxNumberOfMessages = 10,
                WaitTimeSeconds = 2,
                MessageSystemAttributeNames = ["AWSTraceHeader"],
            });
            foreach (var msg in resp.Messages ?? [])
            {
                await sqs.DeleteMessageAsync(new DeleteMessageRequest { QueueUrl = url, ReceiptHandle = msg.ReceiptHandle });
                seen[msg.Body] = msg;
            }
        }

        var missing = wanted.Except(seen.Keys).Order(StringComparer.Ordinal).ToList();
        if (missing.Count > 0)
        {
            throw new TimeoutException(
                $"within {timeoutSeconds:F0}s the queue never delivered [{string.Join(", ", missing)}]; it delivered " +
                $"[{string.Join(", ", seen.Keys.Order(StringComparer.Ordinal))}]");
        }

        return seen;
    }

    /// <summary>A wait's timeout, bounded by the runner's remaining time so the wait fails with its own message.</summary>
    private static double BudgetSeconds(FidelityContext ctx, double capSeconds)
    {
        var left = ctx.TimeLeftSeconds;
        return Math.Max(1.0, Math.Min(capSeconds, left - 5.0));
    }
}
