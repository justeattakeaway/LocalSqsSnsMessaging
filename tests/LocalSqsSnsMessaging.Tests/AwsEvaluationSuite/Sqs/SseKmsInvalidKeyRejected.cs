// Translated from behavioral/sqs/check_sse_kms_invalid_key_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Text.RegularExpressions;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sqs;

/// <summary>sqs.sse-kms-invalid-key-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed partial class SseKmsInvalidKeyRejected
{
    // AWS appends the request id of the inner service call to this message; it is the only part
    // that varies per call.
    [GeneratedRegex("Request ID: [0-9A-Za-z-]+")]
    private static partial Regex RequestId();

    private static string? Stable(string? message)
        => message is null ? null : RequestId().Replace(message, "Request ID: <request-id>");

    /// <summary>
    /// SetQueueAttributes accepts a nonexistent KMS alias, but the next SendMessage fails with
    /// KMS.NotFoundException and enqueues nothing; clearing the key restores delivery.
    /// </summary>
    [Test]
    [DisplayName("sqs.sse-kms-invalid-key-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    [KnownFidelityGap(FidelityGap.Validation, "expected SendMessage to a queue with a nonexistent KmsMasterKeyId alias to fail with KMS.NotFoundException, but it succeeded")]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sqs = ctx.Sqs;
        var url = (await sqs.CreateQueueAsync(new CreateQueueRequest { QueueName = FidelityContext.UniqueName("sim2real-behav-badkms-") })).QueueUrl;
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(url));

        // Positive control: the fresh, unencrypted queue delivers normally before we touch KMS at all,
        // so a later refusal can only be attributed to the bad key and not to some other queue defect.
        await sqs.SendMessageAsync(new SendMessageRequest { QueueUrl = url, MessageBody = "control-before-bad-key" });
        var control = await PollForMessage(ctx, url);
        Ensure(control.Body == "control-before-bad-key",
            $"expected the positive control to deliver 'control-before-bad-key', got '{control.Body}'");
        await sqs.DeleteMessageAsync(new DeleteMessageRequest { QueueUrl = url, ReceiptHandle = control.ReceiptHandle });

        // SQS resolves the key only when it encrypts, so setting a missing alias succeeds.
        const string badAlias = "alias/sim2real-definitely-does-not-exist-9f3a1c";
        await sqs.SetQueueAttributesAsync(new SetQueueAttributesRequest { QueueUrl = url, Attributes = new() { ["KmsMasterKeyId"] = badAlias } });

        var (code, message) = await ErrorCode(() => sqs.SendMessageAsync(new SendMessageRequest { QueueUrl = url, MessageBody = "should-not-be-delivered" }));
        Ensure(code == "KMS.NotFoundException", $"expected KMS.NotFoundException, got '{code}'");
        // SQS relays the KMS error text verbatim; the KMS request id is the only varying part.
        var expected =
            $"Alias arn:aws:kms:{ctx.Region}:{ctx.Account}:{badAlias} is not found. (Service: AWSKMS; " +
            "Status Code: 400; Error Code: NotFoundException; Request ID: <request-id>; Proxy: null)";
        Ensure(Stable(message) == expected, $"expected message '{expected}', got '{message}'");

        // Effect absent: nothing was actually enqueued.
        var count = (await sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest
        {
            QueueUrl = url,
            AttributeNames = ["ApproximateNumberOfMessages"],
        })).Attributes!["ApproximateNumberOfMessages"];
        Ensure(count == "0", $"expected 0 messages after the rejected send, saw '{count}'");

        // Positive control: clear the bad key on the same queue and delivery works again.
        await sqs.SetQueueAttributesAsync(new SetQueueAttributesRequest { QueueUrl = url, Attributes = new() { ["KmsMasterKeyId"] = "" } });
        await sqs.SendMessageAsync(new SendMessageRequest { QueueUrl = url, MessageBody = "delivered-after-fixing-key" });
        var received = await PollForMessage(ctx, url);
        Ensure(received.Body == "delivered-after-fixing-key",
            $"expected 'delivered-after-fixing-key' once the bad key is cleared, got '{received.Body}'");
        await sqs.DeleteMessageAsync(new DeleteMessageRequest { QueueUrl = url, ReceiptHandle = received.ReceiptHandle });
    }

    /// <summary>Call fn(); return its botocore error (code, message). An unexpected success raises.</summary>
    private static async Task<(string? Code, string? Message)> ErrorCode(Func<Task<SendMessageResponse>> fn)
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is not null)
        {
            return (error.Code, error.Message);
        }

        throw Fail($"expected the send to be rejected, but it SUCCEEDED and returned {{'MD5OfMessageBody': '{result!.MD5OfMessageBody}', 'MessageId': '{result.MessageId}'}}");
    }

    private static async Task<Message> PollForMessage(FidelityContext ctx, string url, double timeoutSeconds = 30.0)
    {
        var sqs = ctx.Sqs;
        var deadline = FidelityTime.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(timeoutSeconds))
        {
            var resp = await sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = url, MaxNumberOfMessages = 1, WaitTimeSeconds = 5 });
            var msgs = resp.Messages ?? [];
            if (msgs.Count > 0)
            {
                return msgs[0];
            }
        }

        throw Fail("no message received within timeout");
    }
}
