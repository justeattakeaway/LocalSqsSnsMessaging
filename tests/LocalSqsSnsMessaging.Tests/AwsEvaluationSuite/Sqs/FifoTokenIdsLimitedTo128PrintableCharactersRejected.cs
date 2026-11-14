// Translated from behavioral/sqs/check_fifo_token_ids_limited_to_128_printable_characters_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sqs;

/// <summary>sqs.fifo-token-ids-limited-to-128-printable-characters-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class FifoTokenIdsLimitedTo128PrintableCharactersRejected
{
    private static readonly Dictionary<string, string> Tags = new()
    {
        ["sim2real"] = "fifo-token-ids-limited-to-128-printable-characters-rejected",
    };

    private const string Punct = "!\"#$%&'()*+,-./:;<=>?@[\\]^_`{|}~";

    /// <summary>
    /// FIFO group, dedup and receive-attempt ids over 128 chars or with spaces are refused.
    ///
    /// Refused sends enqueue nothing; 128-char and all-punctuation ids are accepted and kept verbatim.
    /// </summary>
    [Test]
    [DisplayName("sqs.fifo-token-ids-limited-to-128-printable-characters-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sqs = ctx.Sqs;
        var url = (await sqs.CreateQueueAsync(new CreateQueueRequest
        {
            QueueName = FidelityContext.UniqueName("sim2real-behav-fifo-ids-") + ".fifo",
            Attributes = new() { ["FifoQueue"] = "true" }, Tags = new(Tags),
        })).QueueUrl;
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(url));

        foreach (var bad in new[] { new string('g', 129), "g g" })
        {
            var err = await Error(() => sqs.SendMessageAsync(new SendMessageRequest
            {
                QueueUrl = url, MessageBody = "refused", MessageGroupId = bad, MessageDeduplicationId = "d0",
            }));
            Ensure((err.Code, err.Message) == ("InvalidParameterValue", Msg("MessageGroupId", bad)),
                $"MessageGroupId of {bad.Length} chars '{bad[..Math.Min(8, bad.Length)]}' answered {err}");
        }

        foreach (var bad in new[] { new string('d', 129), "d d", "" })
        {
            var err = await Error(() => sqs.SendMessageAsync(new SendMessageRequest
            {
                QueueUrl = url, MessageBody = "refused", MessageGroupId = "g", MessageDeduplicationId = bad,
            }));
            Ensure((err.Code, err.Message) == ("InvalidParameterValue", Msg("MessageDeduplicationId", bad)),
                $"MessageDeduplicationId of {bad.Length} chars '{bad[..Math.Min(8, bad.Length)]}' answered {err}");
        }

        foreach (var bad in new[] { new string('r', 129), "r r" })
        {
            var err = await Error(() => sqs.ReceiveMessageAsync(new ReceiveMessageRequest
            {
                QueueUrl = url, ReceiveRequestAttemptId = bad, WaitTimeSeconds = 1,
            }));
            Ensure((err.Code, err.Message) == ("InvalidParameterValue", Msg("ReceiveRequestAttemptId", bad)),
                $"ReceiveRequestAttemptId of {bad.Length} chars '{bad[..Math.Min(8, bad.Length)]}' answered {err}");
        }

        var empty = await sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = url, MaxNumberOfMessages = 10, WaitTimeSeconds = 2 });
        Ensure((empty.Messages ?? []).Count == 0,
            $"refused sends enqueued [{string.Join(", ", (empty.Messages ?? []).Select(m => m.Body))}]");

        (string Gid, string Did)[] accepted = [(new string('g', 128), new string('d', 128)), (Punct, Punct)];
        foreach (var (gid, did) in accepted)
        {
            await sqs.SendMessageAsync(new SendMessageRequest
            {
                QueueUrl = url, MessageBody = $"ok-{gid.Length}", MessageGroupId = gid,
                MessageDeduplicationId = did,
            });
        }

        var got = new Dictionary<string, (string, string)>();
        var deadline = FidelityTime.StartNew();
        var attempt = new string('a', 128);
        while (got.Count < 2 && deadline.Elapsed < TimeSpan.FromSeconds(30))
        {
            foreach (var m in (await sqs.ReceiveMessageAsync(new ReceiveMessageRequest
            {
                QueueUrl = url, MaxNumberOfMessages = 10, WaitTimeSeconds = 2,
                ReceiveRequestAttemptId = attempt,
                MessageSystemAttributeNames = ["MessageGroupId", "MessageDeduplicationId"],
            })).Messages ?? [])
            {
                got[m.Body] = (m.Attributes!["MessageGroupId"],
                               m.Attributes!["MessageDeduplicationId"]);
                await sqs.DeleteMessageAsync(new DeleteMessageRequest { QueueUrl = url, ReceiptHandle = m.ReceiptHandle });
            }

            attempt = attempt[..^1] + (attempt[^1] == 'a' ? "b" : "a");
        }

        var want = accepted.ToDictionary(a => $"ok-{a.Gid.Length}", a => (a.Gid, a.Did));
        Ensure(got.Count == want.Count && want.All(kv => got.TryGetValue(kv.Key, out var v) && v == kv.Value),
            $"boundary ids came back as {{{string.Join(", ", got.Select(kv => $"'{kv.Key}': {kv.Value}"))}}}; expected the 128-character and all-punctuation " +
            "values verbatim");
    }

    /// <summary>The botocore Error dict ``fn`` raises. An unexpected success raises, naming the result.</summary>
    private static async Task<AwsError> Error<T>(Func<Task<T>> fn)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        return error ?? throw Fail($"expected the call to be refused, but it succeeded: {result}");
    }

    private static string Msg(string param, string value) =>
        $"Value {value} for parameter {param} is invalid. Reason: {param} can only include " +
        "alphanumeric and punctuation characters. 1 to 128 in length.";
}
