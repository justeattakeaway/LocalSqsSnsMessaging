// Translated from behavioral/sqs/check_receive_message_attribute_selectivity.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Diagnostics;
using Amazon.SQS;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sqs;

/// <summary>sqs.receive-message-attribute-selectivity</summary>
[Category("AwsEvaluationSuite")]
public sealed class ReceiveMessageAttributeSelectivity
{
    // A well-formed X-Amzn-Trace-Id value, the only documented MessageSystemAttribute.
    private const string TraceHeader = "Root=1-5759e988-bd862e3fe1be46a994272793;Parent=53995c3f42cd8ad8;Sampled=1";

    /// <summary>
    /// ReceiveMessage returns exactly the projected attributes: none by default, one when named, all
    /// under 'All', system attributes only via MessageSystemAttributeNames (AWSTraceHeader exact), and
    /// MD5OfMessageAttributes is computed over the projected subset.
    /// </summary>
    [Test]
    [DisplayName("sqs.receive-message-attribute-selectivity")]
    [Timeout(FidelityContext.BudgetMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sqs = ctx.Sqs;
        var url = (await sqs.CreateQueueAsync(new CreateQueueRequest { QueueName = FidelityContext.UniqueName("sim2real-behav-") })).QueueUrl;
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(url));
        const int pollS = 25;

        var token = Guid.NewGuid().ToString("N")[..8];
        var body = $"attr-projection-{token}";
        var sent = await sqs.SendMessageAsync(new SendMessageRequest
        {
            QueueUrl = url,
            MessageBody = body,
            MessageAttributes = new()
            {
                ["Author"] = new() { DataType = "String", StringValue = $"sim2real-{token}" },
                ["Version"] = new() { DataType = "Number", StringValue = "7" },
            },
            MessageSystemAttributes = new()
            {
                ["AWSTraceHeader"] = new() { DataType = "String", StringValue = TraceHeader },
            },
        });
        // SendMessage's digest covers BOTH message attributes, i.e. everything stored. The receive
        // legs below compare against it to prove the receive-side digest tracks the PROJECTION.
        // (Python indexes sent["MD5OfMessageAttributes"], a KeyError when the response omits it.)
        var sentMd5 = sent.MD5OfMessageAttributes ?? throw new KeyNotFoundException("'MD5OfMessageAttributes'");
        Ensure(sentMd5.Length > 0, "SendMessage did not return MD5OfMessageAttributes");

        // --- (a) default projection: no attribute content at all --------------------------
        var msg = await Take(sqs, url, timeoutS: pollS);
        Ensure(msg is not null && msg.Body == body, $"default receive got {msg?.Body}");
        Ensure((msg!.MessageAttributes ?? []).Count == 0,
            "MessageAttributes returned without being requested: "
            + Sorted(msg.MessageAttributes?.Keys));
        Ensure((msg.Attributes ?? []).Count == 0,
            "system Attributes returned without being requested: "
            + Sorted(msg.Attributes?.Keys));
        // The receive-side digest is computed over the
        // PROJECTED subset, not over what the message stores, so an unprojected receive omits
        // MD5OfMessageAttributes entirely even though the message does have attributes.
        Ensure(msg.MD5OfMessageAttributes is null,
            "MD5OfMessageAttributes must be absent when no attribute was projected, got "
            + $"'{msg.MD5OfMessageAttributes}'");

        // --- (b) one named attribute: exactly that one ------------------------------------
        msg = await Take(sqs, url, timeoutS: pollS, messageAttributeNames: ["Author"]);
        Ensure(msg is not null, "receive with MessageAttributeNames=['Author'] returned nothing");
        var got = msg.MessageAttributes ?? [];
        Ensure(got.Keys.ToHashSet().SetEquals(["Author"]),
            $"projected ['Author'] but received {Sorted(got.Keys)}");
        Ensure(got["Author"].StringValue == $"sim2real-{token}",
            $"Author value round-trip: '{got["Author"].StringValue}'");
        Ensure(got["Author"].DataType == "String", $"Author DataType '{got["Author"].DataType}'");
        Ensure((msg.Attributes ?? []).Count == 0,
            $"MessageAttributeNames leaked system Attributes: {Sorted(msg.Attributes?.Keys)}");
        // One of two stored attributes was projected, so the digest must be over that subset and
        // therefore differ from the send-time digest over both.
        var partialMd5 = msg.MD5OfMessageAttributes;
        Ensure(!string.IsNullOrEmpty(partialMd5), "a projected attribute must come with MD5OfMessageAttributes");
        Ensure(partialMd5 != sentMd5,
            "the digest for the ['Author'] subset must differ from the send-time digest over both "
            + $"attributes, both were '{partialMd5}'");

        // --- (c) 'All': both, with types preserved ----------------------------------------
        msg = await Take(sqs, url, timeoutS: pollS, messageAttributeNames: ["All"]);
        Ensure(msg is not null, "receive with MessageAttributeNames=['All'] returned nothing");
        got = msg.MessageAttributes ?? [];
        Ensure(got.Keys.ToHashSet().SetEquals(["Author", "Version"]), $"'All' returned {Sorted(got.Keys)}");
        Ensure(got["Version"].DataType == "Number",
            $"Number DataType was not preserved: '{got["Version"].DataType}'");
        Ensure(got["Version"].StringValue == "7",
            $"Number attributes travel as StringValue; got '{got["Version"].StringValue}'");
        Ensure((msg.Attributes ?? []).Count == 0,
            "MessageAttributeNames=['All'] must not project system Attributes, got "
            + Sorted(msg.Attributes?.Keys));
        // Projecting everything reproduces the send-time digest byte-for-byte.
        Ensure(msg.MD5OfMessageAttributes == sentMd5,
            $"'All' must reproduce the send-time attribute digest '{sentMd5}', got "
            + $"'{msg.MD5OfMessageAttributes}'");

        // --- (d) system attributes are a separate namespace, and AWSTraceHeader is exact --
        msg = await Take(sqs, url, timeoutS: pollS, messageSystemAttributeNames: ["AWSTraceHeader"]);
        Ensure(msg is not null, "receive with MessageSystemAttributeNames returned nothing");
        var sysattrs = msg.Attributes ?? [];
        Ensure(sysattrs.Keys.ToHashSet().SetEquals(["AWSTraceHeader"]),
            $"projected ['AWSTraceHeader'] but received system Attributes {Sorted(sysattrs.Keys)}");
        Ensure(sysattrs["AWSTraceHeader"] == TraceHeader,
            $"AWSTraceHeader did not round-trip byte-exactly:\n  sent '{TraceHeader}'\n"
            + $"  got  '{sysattrs["AWSTraceHeader"]}'");
        Ensure((msg.MessageAttributes ?? []).Count == 0,
            "MessageSystemAttributeNames leaked MessageAttributes: "
            + Sorted(msg.MessageAttributes?.Keys));
        Ensure(msg.MD5OfMessageAttributes is null,
            "a system-attribute-only projection projects no message attribute, so its digest must "
            + $"be absent; got '{msg.MD5OfMessageAttributes}'");

        await sqs.DeleteMessageAsync(new DeleteMessageRequest { QueueUrl = url, ReceiptHandle = msg.ReceiptHandle });
    }

    /// <summary>
    /// Receive one message with the requested projection, then re-expose it with
    /// ChangeMessageVisibility(0) so the next projection is taken on the same message.
    /// </summary>
    private static async Task<Message?> Take(IAmazonSQS sqs, string url, double timeoutS,
        List<string>? messageAttributeNames = null, List<string>? messageSystemAttributeNames = null)
    {
        var deadline = Stopwatch.StartNew();
        while (true)
        {
            var request = new ReceiveMessageRequest { QueueUrl = url, MaxNumberOfMessages = 1, WaitTimeSeconds = 1 };
            if (messageAttributeNames is not null)
            {
                request.MessageAttributeNames = messageAttributeNames;
            }

            if (messageSystemAttributeNames is not null)
            {
                request.MessageSystemAttributeNames = messageSystemAttributeNames;
            }

            var msgs = (await sqs.ReceiveMessageAsync(request)).Messages ?? [];
            if (msgs.Count > 0)
            {
                var msg = msgs[0];
                await sqs.ChangeMessageVisibilityAsync(new ChangeMessageVisibilityRequest
                {
                    QueueUrl = url,
                    ReceiptHandle = msg.ReceiptHandle,
                    VisibilityTimeout = 0,
                });
                return msg;
            }

            if (deadline.Elapsed >= TimeSpan.FromSeconds(timeoutS))
            {
                return null;
            }
        }
    }

    private static string Sorted(IEnumerable<string>? keys) =>
        $"[{string.Join(", ", (keys ?? []).Order(StringComparer.Ordinal).Select(k => $"'{k}'"))}]";
}
