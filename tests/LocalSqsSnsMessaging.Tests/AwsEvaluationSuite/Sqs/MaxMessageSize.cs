// Translated from behavioral/sqs/check_max_message_size.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sqs;

/// <summary>sqs.max-message-size</summary>
[Category("AwsEvaluationSuite")]
public sealed class MaxMessageSize
{
    /// <summary>
    /// A 262144-byte message is accepted and round-trips byte-for-byte; a 2 MB message is rejected
    /// with HTTP 413 at the transport layer, before SQS field validation, and is not delivered.
    /// </summary>
    [Test]
    [DisplayName("sqs.max-message-size")]
    [Timeout(FidelityContext.BudgetMilliseconds)]
    [KnownFidelityGap(FidelityGap.Protocol, "expected a 413 (request entity too large) for a 2MB body, got InvalidParameterValue")]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sqs = ctx.Sqs;
        var url = (await sqs.CreateQueueAsync(new CreateQueueRequest { QueueName = FidelityContext.UniqueName("sim2real-behav-") })).QueueUrl;
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(url));
        var bodyAtLimit = new string('x', 256 * 1024);
        await sqs.SendMessageAsync(new SendMessageRequest { QueueUrl = url, MessageBody = bodyAtLimit });
        List<Message> drained = [];
        HashSet<string> seen = [];
        var idle = 0;
        for (var r = 0; r < 12; r++)
        {
            var resp = await sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = url, MaxNumberOfMessages = 1, WaitTimeSeconds = 1 });
            var msgs = resp.Messages ?? [];
            if (msgs.Count == 0)
            {
                idle++;
                if (drained.Count > 0 && idle >= 3)
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
                    drained.Add(m);
                }

                await sqs.DeleteMessageAsync(new DeleteMessageRequest { QueueUrl = url, ReceiptHandle = m.ReceiptHandle });
            }

            if (drained.Count >= 1)
            {
                break;
            }
        }

        Ensure(drained.Count > 0, "message sent at the 262144-byte limit was never delivered");
        Ensure(drained[0].Body == bodyAtLimit,
            $"body at the size limit was altered in transit: len sent={bodyAtLimit.Length}, " +
            $"len received={drained[0].Body.Length}");
        var body2Mb = new string('x', 2 * 1024 * 1024);
        var error = await FidelityContext.CaptureError(() => sqs.SendMessageAsync(new SendMessageRequest { QueueUrl = url, MessageBody = body2Mb }));
        var code = error?.Code;
        Ensure(code == "413", $"expected a 413 (request entity too large) for a 2MB body, got {code ?? "None"}");
        var leftover = (await sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = url, MaxNumberOfMessages = 10, WaitTimeSeconds = 2 })).Messages ?? [];
        Ensure(leftover.Count == 0, $"oversized body was rejected but still delivered: {leftover.Count} message(s)");
    }
}
