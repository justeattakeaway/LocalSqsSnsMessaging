// Translated from behavioral/sqs/check_content_based_dedup_id_is_the_sha256_of_the_body.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sqs;

/// <summary>sqs.content-based-dedup-id-is-the-sha256-of-the-body</summary>
[Category("AwsEvaluationSuite")]
public sealed class ContentBasedDedupIdIsTheSha256OfTheBody
{
    private static readonly Dictionary<string, string> Tags = new() { ["sim2real:managed"] = "true", ["sim2real"] = "cbd-sha256" };

    /// <summary>
    /// Content-based dedup ids are the lowercase hex SHA-256 of the body, sharing one id space with
    /// explicit ids: an equal explicit id collides in either order; the uppercase digest does not.
    /// </summary>
    [Test]
    [DisplayName("sqs.content-based-dedup-id-is-the-sha256-of-the-body")]
    [Timeout(FidelityContext.BudgetMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sqs = ctx.Sqs;
        var token = Guid.NewGuid().ToString("N")[..8];
        var url = (await sqs.CreateQueueAsync(new CreateQueueRequest
        {
            QueueName = FidelityContext.UniqueName("sim2real-behav-") + ".fifo",
            Attributes = new() { ["FifoQueue"] = "true", ["ContentBasedDeduplication"] = "true" },
            Tags = new(Tags),
        })).QueueUrl;
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(url));

        async Task<string> Send(string body, string? dedupId = null)
        {
            var request = new SendMessageRequest { QueueUrl = url, MessageBody = body, MessageGroupId = "g" };
            if (!string.IsNullOrEmpty(dedupId))
            {
                request.MessageDeduplicationId = dedupId;
            }

            return (await sqs.SendMessageAsync(request)).MessageId;
        }

        var first = $"hashed-first-{token}";
        var a = await Send(first);
        var aDup = await Send($"explicit-collides-{token}", Sha256(first));
        Ensure(aDup == a,
            "an explicit MessageDeduplicationId equal to sha256(body) of a content-based send must " +
            $"be deduplicated against it: first '{a}', second '{aDup}'");

        var second = $"hashed-second-{token}";
        var b = await Send($"explicit-first-{token}", Sha256(second));
        var bDup = await Send(second);
        Ensure(bDup == b,
            "a content-based send whose body hashes to an already-used explicit id must be " +
            $"deduplicated against it: explicit '{b}', content-based '{bDup}'");

        // Dedup ids are case-sensitive: the uppercase digest is a different id.
        var third = $"hashed-third-{token}";
        var c = await Send($"explicit-upper-{token}", Sha256(third).ToUpperInvariant());
        var cOther = await Send(third);
        Ensure(cOther != c,
            "deduplication ids are case-sensitive: the uppercase digest must not collide with the " +
            $"content-based send, but both returned '{c}'");

        var bodies = new List<string>();
        var empties = 0;
        var deadline = Stopwatch.StartNew();
        while (empties < 3 && deadline.Elapsed < TimeSpan.FromSeconds(60))
        {
            var batch = (await sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = url, MaxNumberOfMessages = 10, WaitTimeSeconds = 2 })).Messages ?? [];
            empties = batch.Count > 0 ? 0 : empties + 1;
            foreach (var m in batch)
            {
                bodies.Add(m.Body);
                await sqs.DeleteMessageAsync(new DeleteMessageRequest { QueueUrl = url, ReceiptHandle = m.ReceiptHandle });
            }
        }

        List<string> want = [first, $"explicit-first-{token}", $"explicit-upper-{token}", third];
        Ensure(bodies.SequenceEqual(want),
            $"the queue must hold exactly the four non-duplicate sends in order: got [{string.Join(", ", bodies)}], " +
            $"expected [{string.Join(", ", want)}]");
    }

    private static string Sha256(string body) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(body)));
}
