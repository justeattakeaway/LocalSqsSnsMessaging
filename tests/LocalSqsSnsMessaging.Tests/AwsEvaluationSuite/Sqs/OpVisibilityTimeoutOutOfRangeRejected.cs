// Translated from behavioral/sqs/check_op_visibility_timeout_out_of_range_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.SQS;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sqs;

/// <summary>sqs.op-visibility-timeout-out-of-range-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class OpVisibilityTimeoutOutOfRangeRejected
{
    // Exact messages real SQS returns for these refusals.
    private const string MsgHigh = "Value 43201 for parameter VisibilityTimeout is invalid. Reason: VisibilityTimeout must be an integer between 0 and 43200.";
    private const string MsgNeg = "Value -1 for parameter VisibilityTimeout is invalid. Reason: VisibilityTimeout must be an integer between 0 and 43200.";

    private static async Task<AwsError> ClientError(Func<Task<ChangeMessageVisibilityResponse>> fn)
    {
        var (result, error) = await FidelityContext.Capture(fn);
        return error ?? throw Fail($"expected the call to be rejected, but it succeeded: {{HTTPStatusCode: {result!.HttpStatusCode}}}");
    }

    private static async Task<List<Message>> ReceiveMessages(AmazonSQSClient sqs, string url, int minCount, int maxBatch = 10, int attempts = 15)
    {
        var received = new List<Message>();
        var seen = new HashSet<string>();
        var idle = 0;
        for (var i = 0; i < attempts; i++)
        {
            var resp = await sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = url, MaxNumberOfMessages = maxBatch, WaitTimeSeconds = 1 });
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

            if (received.Count >= minCount)
            {
                break;
            }
        }

        return received;
    }

    /// <summary>
    /// VisibilityTimeout above the 12-hour maximum must be rejected, not silently clamped.
    /// </summary>
    [Test]
    [DisplayName("sqs.op-visibility-timeout-out-of-range-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sqs = ctx.Sqs;
        var url = (await sqs.CreateQueueAsync(new CreateQueueRequest { QueueName = FidelityContext.UniqueName("sim2real-behav-") })).QueueUrl;
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(url));
        await sqs.SendMessageAsync(new SendMessageRequest { QueueUrl = url, MessageBody = "range-test" });
        var msgs = await ReceiveMessages(sqs, url, minCount: 1, maxBatch: 1, attempts: 20);
        Ensure(msgs.Count > 0, "message never became visible");
        var handle = msgs[0].ReceiptHandle;

        var err = await ClientError(() => sqs.ChangeMessageVisibilityAsync(new ChangeMessageVisibilityRequest { QueueUrl = url, ReceiptHandle = handle, VisibilityTimeout = 43201 }));
        Ensure(err.Code == "InvalidParameterValue", $"expected InvalidParameterValue for VisibilityTimeout=43201, got {err}");
        Ensure(err.Message == MsgHigh, $"got {err}");

        var errNeg = await ClientError(() => sqs.ChangeMessageVisibilityAsync(new ChangeMessageVisibilityRequest { QueueUrl = url, ReceiptHandle = handle, VisibilityTimeout = -1 }));
        Ensure(errNeg.Code == "InvalidParameterValue", $"expected InvalidParameterValue for VisibilityTimeout=-1, got {errNeg}");
        Ensure(errNeg.Message == MsgNeg, $"got {errNeg}");

        // Positive control: an in-range value is accepted, and the effect is confirmed by an
        // independent read (a fresh receive), not just the mutating call's own 200.
        await sqs.ChangeMessageVisibilityAsync(new ChangeMessageVisibilityRequest { QueueUrl = url, ReceiptHandle = handle, VisibilityTimeout = 0 });
        var redelivered = await ReceiveMessages(sqs, url, minCount: 1, maxBatch: 1, attempts: 10);
        Ensure(redelivered.Count > 0, "VisibilityTimeout=0 must make the message immediately receivable again");
        Ensure(redelivered[0].MessageId == msgs[0].MessageId,
            $"expected the same message '{msgs[0].MessageId}' to reappear, got '{redelivered[0].MessageId}'");
    }
}
