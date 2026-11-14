// Translated from behavioral/sqs/check_changevis0_reexposes.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.SQS;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sqs;

/// <summary>sqs.changevis0-reexposes</summary>
[Category("AwsEvaluationSuite")]
public sealed class Changevis0Reexposes
{
    private static readonly Dictionary<string, string> Tags = new() { ["sim2real:managed"] = "true", ["sim2real"] = "changevis0-reexposes" };

    /// <summary>
    /// ChangeMessageVisibility(0) makes an in-flight message immediately receivable.
    /// </summary>
    [Test]
    [DisplayName("sqs.changevis0-reexposes")]
    [Timeout(FidelityContext.BudgetMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sqs = ctx.Sqs;
        var url = (await sqs.CreateQueueAsync(new CreateQueueRequest { QueueName = FidelityContext.UniqueName("sim2real-behav-"), Tags = new(Tags) })).QueueUrl;
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(url));
        await sqs.SendMessageAsync(new SendMessageRequest { QueueUrl = url, MessageBody = "changevis0" });
        var first = await ReceiveUpTo(sqs, url);
        if (first.Count == 0)
        {
            throw Fail("no message received");
        }

        await sqs.ChangeMessageVisibilityAsync(new ChangeMessageVisibilityRequest
        {
            QueueUrl = url, ReceiptHandle = first[0].ReceiptHandle, VisibilityTimeout = 0,
        });
        var again = await ReceiveUpTo(sqs, url);
        if (again.Count == 0)
        {
            throw Fail("message not re-exposed after VT=0");
        }

        if (again[0].MessageId != first[0].MessageId)
        {
            throw Fail("different message after re-expose");
        }
    }

    // The check inlines this receive loop twice; it is the same 12-attempt, MaxNumberOfMessages=1 loop.
    private static async Task<List<Message>> ReceiveUpTo(AmazonSQSClient sqs, string url)
    {
        var received = new List<Message>();
        var seen = new HashSet<string>();
        var idle = 0;
        for (var attempt = 0; attempt < 12; attempt++)
        {
            var resp = await sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = url, MaxNumberOfMessages = 1, WaitTimeSeconds = 1 });
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
            }

            if (received.Count >= 1)
            {
                break;
            }
        }

        return received;
    }
}
