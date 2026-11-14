// Translated from behavioral/sqs/check_random_body_roundtrip.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sqs;

/// <summary>sqs.random-body-roundtrip</summary>
[Category("AwsEvaluationSuite")]
public sealed class RandomBodyRoundtrip
{
    private static readonly string[] SafeChars =
    [
        .. Enumerable.Range(0x20, 0x7F - 0x20).Select(c => ((char)c).ToString()),
        "\t", "\n", "\r",
        .. TextElements("áéíóúñüßçΩλµ§€—…"),
        .. TextElements("日本語中文한국어"),
        "😀", "🚀", "✅", "🌍",
    ];

    /// <summary>
    /// Random bodies (varied size + charset) round-trip byte-exact with correct MD5.
    /// </summary>
    [Test]
    [DisplayName("sqs.random-body-roundtrip")]
    [Timeout(FidelityContext.BudgetMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sqs = ctx.Sqs;
        // Same seed as the Python's random.Random(0xB0D8); System.Random draws a different (but
        // equally deterministic) sequence from the same character set.
        var rng = new Random(0xB0D8);
        var url = (await sqs.CreateQueueAsync(new CreateQueueRequest
        {
            QueueName = FidelityContext.UniqueName("sim2real-behav-"),
            Tags = new() { ["sim2real:managed"] = "true", ["sim2real"] = "random-body-roundtrip" },
        })).QueueUrl;
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(url));
        var token = Guid.NewGuid().ToString("N")[..8];
        var bodies = new[] { 1, 20, 500, 5_000, 60_000 }
            .Select((size, i) => $"{i}|{token}|{RandomBody(rng, size)}")
            .ToHashSet(StringComparer.Ordinal);
        var md5s = bodies.ToDictionary(body => body, Md5Hex, StringComparer.Ordinal);
        foreach (var body in bodies)
        {
            await sqs.SendMessageAsync(new SendMessageRequest { QueueUrl = url, MessageBody = body });
        }

        List<Message> received = [];
        HashSet<string> seen = [];
        var idle = 0;
        for (var _ = 0; _ < 20; _++)
        {
            var resp = await sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = url, MaxNumberOfMessages = 10, WaitTimeSeconds = 1 });
            var msgs = resp.Messages ?? [];
            if (msgs.Count == 0)
            {
                idle++;
                if (received.Count > 0 && idle >= 3)
                {
                    break;
                }

                continue;
            }

            idle = 0;
            foreach (var m in msgs)
            {
                if (seen.Add(m.MessageId))
                {
                    received.Add(m);
                }

                await sqs.DeleteMessageAsync(new DeleteMessageRequest { QueueUrl = url, ReceiptHandle = m.ReceiptHandle });
            }

            if (received.Count >= bodies.Count)
            {
                break;
            }
        }

        var got = received.Select(m => m.Body).ToHashSet(StringComparer.Ordinal);
        Ensure(got.SetEquals(bodies), $"body set mismatch: sent {bodies.Count}, got {got.Count} distinct");
        foreach (var msg in received)
        {
            var expected = md5s[msg.Body];
            Ensure(msg.MD5OfBody == expected, $"MD5OfBody mismatch: {msg.MD5OfBody} != {expected}");
        }
    }

    private static string Md5Hex(string body) => Convert.ToHexStringLower(MD5.HashData(Encoding.UTF8.GetBytes(body)));

    private static string RandomBody(Random rng, int nChars)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < nChars; i++)
        {
            sb.Append(SafeChars[rng.Next(SafeChars.Length)]);
        }

        return sb.ToString();
    }

    private static IEnumerable<string> TextElements(string s)
    {
        var e = StringInfo.GetTextElementEnumerator(s);
        while (e.MoveNext())
        {
            yield return e.GetTextElement();
        }
    }
}
