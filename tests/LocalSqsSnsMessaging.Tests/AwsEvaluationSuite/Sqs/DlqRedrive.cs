// Translated from behavioral/sqs/check_dlq_redrive.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Text.Json;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sqs;

/// <summary>sqs.dlq-redrive</summary>
[Category("AwsEvaluationSuite")]
public sealed class DlqRedrive
{
    /// <summary>
    /// After maxReceiveCount is exceeded the message moves to the DLQ, and not on its first
    /// delivery.
    /// </summary>
    [Test]
    [DisplayName("sqs.dlq-redrive")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sqs = ctx.Sqs;
        var dlqUrl = (await sqs.CreateQueueAsync(new CreateQueueRequest { QueueName = FidelityContext.UniqueName("sim2real-behav-") })).QueueUrl;
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(dlqUrl));
        var dlqArn = (await sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest { QueueUrl = dlqUrl, AttributeNames = ["QueueArn"] }))
            .Attributes!["QueueArn"];
        var mainUrl = (await sqs.CreateQueueAsync(new CreateQueueRequest
        {
            QueueName = FidelityContext.UniqueName("sim2real-behav-"),
            Attributes = new()
            {
                ["RedrivePolicy"] = PyDumps(("deadLetterTargetArn", dlqArn), ("maxReceiveCount", 2)),
            },
        })).QueueUrl;
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(mainUrl));
        var body = $"dlq-{Guid.NewGuid().ToString("N")[..8]}";
        await sqs.SendMessageAsync(new SendMessageRequest { QueueUrl = mainUrl, MessageBody = body });

        // AWS moves a message once ReceiveCount exceeds maxReceiveCount; the check asserts only that
        // it survives its first delivery, which avoids pinning an exact off-by-one boundary.
        var receives = 0;
        Message? dlqMessage = null;
        for (var i = 0; i < 8; i++)
        {
            var resp = await sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = mainUrl, MaxNumberOfMessages = 1, WaitTimeSeconds = 1 });
            var got = resp.Messages ?? [];
            receives += got.Count;
            foreach (var msg in got)
            {
                await sqs.ChangeMessageVisibilityAsync(new ChangeMessageVisibilityRequest { QueueUrl = mainUrl, ReceiptHandle = msg.ReceiptHandle, VisibilityTimeout = 0 });
            }

            var dresp = await sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = dlqUrl, MaxNumberOfMessages = 1, WaitTimeSeconds = 1 });
            if (dresp.Messages is { Count: > 0 })
            {
                dlqMessage = dresp.Messages[0];
                break;
            }
        }

        if (dlqMessage is null)
        {
            throw Fail("message never landed in the DLQ");
        }

        if (dlqMessage.Body != body)
        {
            throw Fail("DLQ message body mismatch");
        }

        // Asserted after the loop because the loop breaks as soon as the DLQ holds the message.
        Ensure(receives >= 2,
            $"message reached the DLQ after {receives} delivery(ies); maxReceiveCount: 2 requires it to " +
            "survive at least the first");
        var leftover = (await sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = mainUrl, MaxNumberOfMessages = 10, WaitTimeSeconds = 1 })).Messages ?? [];
        Ensure(leftover.Count == 0, $"expected no messages, got {leftover.Count}");
    }

    /// <summary>Python's <c>json.dumps</c> of a flat dict, with its default separators.</summary>
    private static string PyDumps(params (string Key, object Value)[] items)
        => "{" + string.Join(", ", items.Select(kv => $"{JsonSerializer.Serialize(kv.Key)}: {JsonSerializer.Serialize(kv.Value)}")) + "}";
}
