// Translated from behavioral/sqs/check_send_receive_delete_lifecycle.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Security.Cryptography;
using System.Text;
using Amazon.SQS;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sqs;

/// <summary>sqs.send-receive-delete-lifecycle</summary>
[Category("AwsEvaluationSuite")]
public sealed class SendReceiveDeleteLifecycle
{
    /// <summary>
    /// SendMessage -> ReceiveMessage -> DeleteMessage round trip preserves body/MD5 and
    /// the message is gone afterwards (no longer receivable).
    /// </summary>
    [Test]
    [DisplayName("sqs.send-receive-delete-lifecycle")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sqs = ctx.Sqs;
        var url = (await sqs.CreateQueueAsync(new CreateQueueRequest { QueueName = FidelityContext.UniqueName("sim2real-sqs-lifecycle-") })).QueueUrl;
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(url));

        const string body = "hello-sqs-lifecycle-12345";
        var sendResp = await sqs.SendMessageAsync(new SendMessageRequest { QueueUrl = url, MessageBody = body });
        var expectedMd5 = Convert.ToHexStringLower(MD5.HashData(Encoding.UTF8.GetBytes(body)));
        Ensure(sendResp.MD5OfMessageBody == expectedMd5,
            $"MD5OfMessageBody mismatch: expected {expectedMd5}, got {sendResp.MD5OfMessageBody}");

        var msg = await PollReceiveOne(sqs, url, timeoutSeconds: 20.0);
        Ensure(msg.Body == body, $"Body mismatch: expected {body}, got {msg.Body}");
        Ensure(msg.MD5OfBody == expectedMd5, $"MD5OfBody mismatch: expected {expectedMd5}, got {msg.MD5OfBody}");
        Ensure(msg.MessageId == sendResp.MessageId, $"MessageId mismatch: expected {sendResp.MessageId}, got {msg.MessageId}");

        await sqs.DeleteMessageAsync(new DeleteMessageRequest { QueueUrl = url, ReceiptHandle = msg.ReceiptHandle });

        // after delete, the message must never be receivable again
        var deadline = FidelityTime.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(10))
        {
            var resp = await sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = url, MaxNumberOfMessages = 10, WaitTimeSeconds = 2 });
            Ensure(resp.Messages is null or { Count: 0 }, "deleted message was redelivered");
        }
    }

    private static async Task<Message> PollReceiveOne(AmazonSQSClient sqs, string url, double timeoutSeconds)
    {
        var deadline = FidelityTime.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(timeoutSeconds))
        {
            var resp = await sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = url, MaxNumberOfMessages = 1, WaitTimeSeconds = 5 });
            if (resp.Messages is { Count: > 0 } messages)
            {
                return messages[0];
            }
        }

        throw Fail($"no message received within {timeoutSeconds}s");
    }
}
