// Translated from behavioral/sqs/check_visibility_extension_capped_at_twelve_hours_from_receive_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.Runtime;
using Amazon.SQS;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sqs;

/// <summary>sqs.visibility-extension-capped-at-twelve-hours-from-receive-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class VisibilityExtensionCappedAtTwelveHoursFromReceiveRejected
{
    private const int MaxS = 43200;
    private static readonly string CapMessage =
        $"Value {MaxS} for parameter VisibilityTimeout is invalid. Reason: Total VisibilityTimeout " +
        $"for the message is beyond the limit [{MaxS} seconds].";

    private static Dictionary<string, string> Tags() => new() { ["sim2real:managed"] = "true", ["sim2real"] = "vt-twelve-hour-cap" };

    /// <summary>
    /// ChangeMessageVisibility cannot push visibility past 12 hours from receive: 43200s soon after
    /// a receive is refused though in range, a shorter extension is accepted, and in the batch form the
    /// over-cap entry fails alone.
    /// </summary>
    [Test]
    [DisplayName("sqs.visibility-extension-capped-at-twelve-hours-from-receive-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sqs = ctx.Sqs;
        var token = Guid.NewGuid().ToString("N")[..8];
        var url = (await sqs.CreateQueueAsync(new CreateQueueRequest { QueueName = FidelityContext.UniqueName("sim2real-behav-"), Tags = Tags() })).QueueUrl;
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(url));

        var bodies = Enumerable.Range(0, 3).Select(i => $"cap-{i}-{token}").ToList();
        foreach (var b in bodies)
        {
            await sqs.SendMessageAsync(new SendMessageRequest { QueueUrl = url, MessageBody = b });
        }

        // Received under the full 12-hour timeout, so any further extension crosses the cap.
        var got = await ReceiveAll(sqs, url, 3, MaxS);
        Ensure(got.Select(m => m.Body).Order(StringComparer.Ordinal).SequenceEqual(bodies.Order(StringComparer.Ordinal)),
            $"expected to receive [{string.Join(", ", bodies.Order(StringComparer.Ordinal))}], got [{string.Join(", ", got.Select(m => m.Body).Order(StringComparer.Ordinal))}]");
        var handle = got.ToDictionary(m => m.Body, m => m.ReceiptHandle, StringComparer.Ordinal);
        // The cap is counted from the receive: retry until a measurable instant has passed and the
        // 43200 s extension crosses it. An accepted try only re-arms the same 12 h-from-receive cap.
        string? code = null;
        string? msg = null;
        var left = ctx.TimeLeftSeconds;
        var clock = FidelityTime.StartNew();
        var capDeadline = TimeSpan.FromSeconds(Math.Min(10, left));
        var deadline = capDeadline;
        while (clock.Elapsed < deadline)
        {
            try
            {
                await sqs.ChangeMessageVisibilityAsync(new ChangeMessageVisibilityRequest { QueueUrl = url, ReceiptHandle = handle[bodies[0]], VisibilityTimeout = MaxS });
            }
            catch (AmazonServiceException exc)
            {
                (code, msg) = (exc.ErrorCode, exc.Message);
                break;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(1));
        }

        Ensure((code, msg) == ("InvalidParameterValue", CapMessage),
            $"extending past 12h from receive gave ({code ?? "None"}, {msg ?? "None"})");

        // Positive control: an extension that stays under the cap is accepted.
        await sqs.ChangeMessageVisibilityAsync(new ChangeMessageVisibilityRequest { QueueUrl = url, ReceiptHandle = handle[bodies[0]], VisibilityTimeout = MaxS - 600 });

        // Same cap clock for the batch: retry until its over-cap entry is refused.
        ChangeMessageVisibilityBatchResponse resp;
        while (true)
        {
            resp = await sqs.ChangeMessageVisibilityBatchAsync(new ChangeMessageVisibilityBatchRequest
            {
                QueueUrl = url,
                Entries =
                [
                    new ChangeMessageVisibilityBatchRequestEntry { Id = "over", ReceiptHandle = handle[bodies[1]], VisibilityTimeout = MaxS },
                    new ChangeMessageVisibilityBatchRequestEntry { Id = "release", ReceiptHandle = handle[bodies[2]], VisibilityTimeout = 0 },
                ],
            });
            if ((resp.Failed ?? []).Any(e => e.Id == "over") || clock.Elapsed >= capDeadline)
            {
                break;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(1));
        }

        var ok = (resp.Successful ?? []).Select(e => e.Id).ToList();
        var failed = (resp.Failed ?? []).ToDictionary(e => e.Id, e => (e.Code, e.SenderFault, e.Message), StringComparer.Ordinal);
        Ensure(ok.SequenceEqual(["release"]), $"batch Successful ids [{string.Join(", ", ok)}], expected ['release']");
        Ensure(failed.Count == 1 && failed.TryGetValue("over", out var over) && over == ("InvalidParameterValue", true, CapMessage),
            $"batch Failed entries {{{string.Join(", ", failed.Select(kv => $"'{kv.Key}': {kv.Value}"))}}}");

        // The applied entry really re-exposed its message; the refused ones stay hidden.
        var seen = new List<string>();
        var receiveDeadline = FidelityTime.StartNew();
        while (!seen.Contains(bodies[2]) && receiveDeadline.Elapsed < TimeSpan.FromSeconds(30))
        {
            foreach (var m in (await sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = url, MaxNumberOfMessages = 10, WaitTimeSeconds = 2 })).Messages ?? [])
            {
                seen.Add(m.Body);
                await sqs.DeleteMessageAsync(new DeleteMessageRequest { QueueUrl = url, ReceiptHandle = m.ReceiptHandle });
            }
        }

        Ensure(seen.SequenceEqual([bodies[2]]),
            "only the message released by the batch's VisibilityTimeout=0 entry may be " +
            $"receivable; got [{string.Join(", ", seen)}], expected ['{bodies[2]}']");
    }

    private static async Task<List<Message>> ReceiveAll(AmazonSQSClient sqs, string url, int n, int vt)
    {
        var got = new List<Message>();
        var deadline = FidelityTime.StartNew();
        while (got.Count < n && deadline.Elapsed < TimeSpan.FromSeconds(30))
        {
            got.AddRange((await sqs.ReceiveMessageAsync(new ReceiveMessageRequest
            {
                QueueUrl = url,
                MaxNumberOfMessages = 10,
                WaitTimeSeconds = 2,
                VisibilityTimeout = vt,
            })).Messages ?? []);
        }

        return got;
    }
}
