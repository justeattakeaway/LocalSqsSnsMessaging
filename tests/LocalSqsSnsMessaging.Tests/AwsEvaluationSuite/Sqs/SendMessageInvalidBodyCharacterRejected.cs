// Translated from behavioral/sqs/check_send_message_invalid_body_character_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Diagnostics;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sqs;

/// <summary>sqs.send-message-invalid-body-character-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class SendMessageInvalidBodyCharacterRejected
{
    private const string AllowedSet = "#x9 | #xA | #xD | [#x20-#xD7FF] | [#xE000-#xFFFD] | [#x10000-#x10FFFF]";

    private static readonly (string Ch, string Shown)[] Refused =
    [
        ("\x00", "#x0"),
        ("\x01", "#x1"),
        ("\x1f", "#x1F"),
        ("￾", "#xFFFE"),
        ("￿", "#xFFFF"),
    ];

    private static readonly string[] Accepted =
    [
        "tab\tnewline\ncarriage\r",
        "edges \x20퟿�",
        "astral \U00010000\U0010ffff\U0001f600",
    ];

    /// <summary>
    /// SendMessage refuses a body with a character outside the XML 1.0 set with
    /// InvalidMessageContents naming it and enqueues nothing; edge characters of the allowed
    /// ranges (tab, CR, U+D7FF, U+E000, U+FFFD, U+10000, U+10FFFF) round-trip byte-exact.
    /// </summary>
    [Test]
    [DisplayName("sqs.send-message-invalid-body-character-rejected")]
    [Timeout(FidelityContext.BudgetMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sqs = ctx.Sqs;
        var url = (await sqs.CreateQueueAsync(new CreateQueueRequest { QueueName = FidelityContext.UniqueName("sim2real-behav-bodychar-") })).QueueUrl;
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(url));

        foreach (var body in Accepted)
        {
            var err = await FidelityContext.CaptureError(() => sqs.SendMessageAsync(new SendMessageRequest { QueueUrl = url, MessageBody = body }));
            if (err is not null)
            {
                throw new FidelityAssertionException($"body '{body}' must be accepted, got {err}", err.Exception);
            }
        }

        foreach (var (ch, shown) in Refused)
        {
            var err = await Error(() => sqs.SendMessageAsync(new SendMessageRequest { QueueUrl = url, MessageBody = $"bad{ch}body" }));
            var want = $"Invalid binary character '{shown}' was found in the message body, the set of "
                       + $"allowed characters is {AllowedSet}.";
            Ensure(err.Code == "InvalidMessageContents",
                $"a body containing U+{(int)ch[0]:X4} gave code '{err.Code}'");
            Ensure(err.Message == want,
                $"a body containing U+{(int)ch[0]:X4}: expected message '{want}', got '{err.Message}'");
        }

        List<string> bodies = [];
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(30))
        {
            var msgs = (await sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = url, MaxNumberOfMessages = 10, WaitTimeSeconds = 2 })).Messages ?? [];
            if (msgs.Count == 0 && bodies.Count >= Accepted.Length)
            {
                break;
            }

            foreach (var m in msgs)
            {
                bodies.Add(m.Body);
                await sqs.DeleteMessageAsync(new DeleteMessageRequest { QueueUrl = url, ReceiptHandle = m.ReceiptHandle });
            }
        }

        Ensure(bodies.Order(StringComparer.Ordinal).SequenceEqual(Accepted.Order(StringComparer.Ordinal)),
            "the queue must hold exactly the accepted bodies, byte-exact, and no refused one; "
            + $"received {bodies.Count} bodies");
    }

    /// <summary>
    /// The botocore Error dict <paramref name="fn"/> raises. An unexpected success raises too, naming what came
    /// back: a body real SQS refuses that is accepted must not read as a code mismatch.
    /// </summary>
    private static async Task<AwsError> Error<T>(Func<Task<T>> fn)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        return error ?? throw Fail($"expected the call to be refused, but it succeeded: {result}");
    }
}
