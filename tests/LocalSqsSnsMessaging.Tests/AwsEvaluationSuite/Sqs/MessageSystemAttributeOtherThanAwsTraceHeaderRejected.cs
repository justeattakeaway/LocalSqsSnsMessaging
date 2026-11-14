// Translated from behavioral/sqs/check_message_system_attribute_other_than_aws_trace_header_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Diagnostics;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sqs;

/// <summary>sqs.message-system-attribute-other-than-aws-trace-header-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class MessageSystemAttributeOtherThanAwsTraceHeaderRejected
{
    private const string Scenario = "sqs-system-attribute-name";

    private const string Trace = "Root=1-5759e988-bd862e3fe1be46a994272793;Parent=53995c3f42cd8ad8;Sampled=1";

    /// <summary>
    /// AWSTraceHeader is the only system attribute a producer may set: 'traceparent' is refused in
    /// SendMessage and fails alone (SenderFault) in SendMessageBatch, nothing refused is enqueued,
    /// and accepted messages arrive carrying their AWSTraceHeader.
    /// </summary>
    [Test]
    [DisplayName("sqs.message-system-attribute-other-than-aws-trace-header-rejected")]
    [Timeout(FidelityContext.BudgetMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sqs = ctx.Sqs;
        var url = await CreateQueue(ctx);
        static Dictionary<string, MessageSystemAttributeValue> TraceAttrs() => new() { ["AWSTraceHeader"] = new() { DataType = "String", StringValue = Trace } };
        static Dictionary<string, MessageSystemAttributeValue> Bogus() => new()
        {
            ["traceparent"] = new() { DataType = "String", StringValue = "00-0af7651916cd43dd8448eb211c80319c-b7ad6b7169203331-01" },
        };
        const string expected = "Message system attribute name 'traceparent' is invalid.";

        var (code, message) = await Error(() => sqs.SendMessageAsync(new SendMessageRequest
        {
            QueueUrl = url,
            MessageBody = "sim2real-refused-single",
            MessageSystemAttributes = Bogus(),
        }));
        Ensure((code, message) == ("InvalidParameterValue", expected),
            $"SendMessage with system attribute 'traceparent': got '{code}' '{message}'");

        await sqs.SendMessageAsync(new SendMessageRequest { QueueUrl = url, MessageBody = "sim2real-accepted-single", MessageSystemAttributes = TraceAttrs() });
        var batch = await sqs.SendMessageBatchAsync(new SendMessageBatchRequest
        {
            QueueUrl = url,
            Entries =
            [
                new() { Id = "good", MessageBody = "sim2real-accepted-batch", MessageSystemAttributes = TraceAttrs() },
                new() { Id = "bad", MessageBody = "sim2real-refused-batch", MessageSystemAttributes = Bogus() },
            ],
        });
        Ensure((batch.Successful ?? []).Select(e => e.Id).SequenceEqual(["good"]),
            $"only the AWSTraceHeader entry may succeed, got [{string.Join(", ", (batch.Successful ?? []).Select(e => e.Id))}]");
        var failed = (batch.Failed ?? []).Select(e => (e.Id, e.Code, e.Message, e.SenderFault!.Value)).ToList();
        Ensure(failed.SequenceEqual([("bad", "InvalidParameterValue", expected, true)]),
            $"the 'traceparent' entry must fail alone with the same refusal, got [{string.Join(", ", failed)}]");

        var accepted = new HashSet<string> { "sim2real-accepted-single", "sim2real-accepted-batch" };
        var seen = await ReceiveAll(ctx, url, accepted, messageSystemAttributeNames: ["AWSTraceHeader"]);
        foreach (var (k, v) in await ReceiveWindow(ctx, url, 5.0))
        {
            seen[k] = v;
        }

        foreach (var body in accepted)
        {
            var got = seen[body].Attributes?.GetValueOrDefault("AWSTraceHeader");
            Ensure(got == Trace, $"'{body}' arrived with AWSTraceHeader '{got}'");
        }

        Ensure(seen.Keys.ToHashSet().SetEquals(accepted),
            $"refused messages were enqueued: [{string.Join(", ", seen.Keys.Except(accepted).Order(StringComparer.Ordinal))}]");
    }

    /// <summary>The (Code, Message) of the ClientError ``fn`` raises. An unexpected success raises too.</summary>
    private static async Task<(string? Code, string Message)> Error<T>(Func<Task<T>> fn)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is not null)
        {
            return (error.Code, error.Message ?? "");
        }

        throw Fail($"expected the call to be refused, but it succeeded: {result}");
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

    /// <summary>
    /// Receive (and delete) until every body in ``wanted`` has been seen; body -> message for
    /// EVERY message seen on the way.
    /// </summary>
    private static async Task<Dictionary<string, Message>> ReceiveAll(
        FidelityContext ctx, string url, HashSet<string> wanted, double timeoutSeconds = 60.0, List<string>? messageSystemAttributeNames = null)
    {
        var sqs = ctx.Sqs;
        var seen = new Dictionary<string, Message>();
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(timeoutSeconds) && !wanted.IsSubsetOf(seen.Keys))
        {
            var request = new ReceiveMessageRequest { QueueUrl = url, MaxNumberOfMessages = 10, WaitTimeSeconds = 2 };
            if (messageSystemAttributeNames is not null)
            {
                request.MessageSystemAttributeNames = messageSystemAttributeNames;
            }

            foreach (var msg in (await sqs.ReceiveMessageAsync(request)).Messages ?? [])
            {
                await sqs.DeleteMessageAsync(new DeleteMessageRequest { QueueUrl = url, ReceiptHandle = msg.ReceiptHandle });
                seen[msg.Body] = msg;
            }
        }

        var missing = wanted.Except(seen.Keys).Order(StringComparer.Ordinal).ToList();
        Ensure(missing.Count == 0,
            $"within {timeoutSeconds}s the queue never delivered [{string.Join(", ", missing)}]; it delivered [{string.Join(", ", seen.Keys.Order(StringComparer.Ordinal))}]");
        return seen;
    }

    /// <summary>Receive (and delete) everything delivered within ``window_s``; body -> message.</summary>
    private static async Task<Dictionary<string, Message>> ReceiveWindow(FidelityContext ctx, string url, double windowSeconds)
    {
        var sqs = ctx.Sqs;
        var seen = new Dictionary<string, Message>();
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(windowSeconds))
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
