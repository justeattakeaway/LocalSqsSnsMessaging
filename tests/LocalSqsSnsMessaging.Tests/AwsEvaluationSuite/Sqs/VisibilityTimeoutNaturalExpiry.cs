// Translated from behavioral/sqs/check_visibility_timeout_natural_expiry.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.SQS;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sqs;

/// <summary>sqs.visibility-timeout-natural-expiry</summary>
[Category("AwsEvaluationSuite")]
public sealed class VisibilityTimeoutNaturalExpiry
{
    private const string Check = "visibility-timeout-natural-expiry";

    /// <summary>
    /// A received message is hidden immediately, then becomes visible again once its 2-second
    /// visibility timeout lapses on its own, with no ChangeMessageVisibility call.
    /// </summary>
    [Test]
    [DisplayName("sqs.visibility-timeout-natural-expiry")]
    [Timeout(FidelityContext.BudgetMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sqs = ctx.Sqs;
        var url = (await sqs.CreateQueueAsync(new CreateQueueRequest
        {
            QueueName = FidelityContext.UniqueName("sim2real-behav-"),
            Attributes = new() { ["VisibilityTimeout"] = "2" },
            Tags = new() { ["sim2real-check"] = Check },
        })).QueueUrl;
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(url));
        var body = $"vt-expiry-{Guid.NewGuid().ToString("N")[..8]}";
        await sqs.SendMessageAsync(new SendMessageRequest { QueueUrl = url, MessageBody = body });
        var first = await ReceiveUpTo(sqs, url, attempts: 12);
        Ensure(first.Count > 0, "no message received");
        // Immediately hidden while in-flight.
        var resp = await sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = url, MaxNumberOfMessages = 1, WaitTimeSeconds = 0 });
        Ensure(resp.Messages is null or { Count: 0 }, "message still visible immediately after receive (VT not applied)");
        // After the 2s VT lapses it must be receivable again — WITHOUT any CMV call. No fixed sleep:
        // the retry loop below is the bounded wait, long-polling past the VT deadline.
        var again = await ReceiveUpTo(sqs, url, attempts: 5);
        Ensure(again.Count > 0, "message never re-appeared after visibility timeout lapsed");
        Ensure(again[0].MessageId == first[0].MessageId, "different message after visibility timeout expiry");
    }

    // The check inlines this loop twice (12 and 5 attempts); it is factored out here unchanged.
    private static async Task<List<Message>> ReceiveUpTo(AmazonSQSClient sqs, string url, int attempts)
    {
        var got = new List<Message>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var idle = 0;
        for (var i = 0; i < attempts; i++)
        {
            var resp = await sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = url, MaxNumberOfMessages = 1, WaitTimeSeconds = 1 });
            var msgs = resp.Messages ?? [];
            if (msgs.Count == 0)
            {
                idle++;
                if (got.Count > 0 && idle >= 3)
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
                    got.Add(m);
                }
            }

            if (got.Count >= 1)
            {
                break;
            }
        }

        return got;
    }
}
