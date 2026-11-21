// Translated from behavioral/sqs/check_purge_queue_on_a_deleted_queue_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit abd0811).
using Amazon.Runtime;
using Amazon.SQS;
using Amazon.SQS.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.Sqs;

/// <summary>sqs.purge-queue-on-a-deleted-queue-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class PurgeQueueOnADeletedQueueRejected
{
    private const string Check = "purge-queue-on-a-deleted-queue-rejected";
    private const string NotFound = "AWS.SimpleQueueService.NonExistentQueue";
    private const string NotFoundMsg = "The specified queue does not exist.";
    private const double SettleSeconds = 60.0;

    /// <summary>
    /// PurgeQueue on a queue deleted earlier in the check is refused with NonExistentQueue
    /// after an independent read already reports the same queue gone, and a sibling queue still
    /// accepts PurgeQueue and empties.
    /// </summary>
    [Test]
    [DisplayName("sqs.purge-queue-on-a-deleted-queue-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sqs = ctx.Sqs;
        var @base = FidelityContext.UniqueName("sim2real-behav-purgedel-");
        var doomed = (await sqs.CreateQueueAsync(new CreateQueueRequest { QueueName = @base + "-gone", Tags = new() { ["sim2real"] = Check } })).QueueUrl;
        ctx.OnCleanup(() => DeleteQuietly(sqs, doomed));
        var sibling = (await sqs.CreateQueueAsync(new CreateQueueRequest { QueueName = @base + "-kept", Tags = new() { ["sim2real"] = Check } })).QueueUrl;
        ctx.OnCleanup(() => DeleteQuietly(sqs, sibling));

        // Positive control: PurgeQueue on a live queue removes the message it holds.
        await sqs.SendMessageAsync(sibling, "purge-me");
        await sqs.PurgeQueueAsync(sibling);
        var deadline = FidelityTime.StartNew();
        List<Message>? left = null;
        while (deadline.Elapsed < TimeSpan.FromSeconds(SettleSeconds))
        {
            left = (await sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = sibling, MaxNumberOfMessages = 10, WaitTimeSeconds = 2 })).Messages ?? [];
            if (left.Count == 0)
            {
                break;
            }
        }

        Ensure(left is { Count: 0 },
            $"PurgeQueue on the live sibling left messages behind: [{string.Join(", ", (left ?? []).Select(m => m.Body))}]");

        await sqs.DeleteQueueAsync(doomed);

        // Deletion is eventually consistent; poll an independent read (GetQueueAttributes, not
        // PurgeQueue) until it reports the queue gone, then hold PurgeQueue to the same refusal.
        AwsError? readErr = null;
        deadline = FidelityTime.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(SettleSeconds))
        {
            readErr = await FidelityContext.CaptureError(() => sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest { QueueUrl = doomed, AttributeNames = ["QueueArn"] }));
            if (readErr is not null)
            {
                break;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(3));
        }

        Ensure((readErr?.Code, readErr?.Message) == (NotFound, NotFoundMsg),
            $"GetQueueAttributes on the deleted queue answered {readErr?.ToString() ?? "{}"} within {SettleSeconds}s");

        var purgeErr = await FidelityContext.CaptureError(() => sqs.PurgeQueueAsync(doomed))
            ?? throw Fail("expected the call to be refused, but it succeeded");
        Ensure((purgeErr.Code, purgeErr.Message) == (NotFound, NotFoundMsg), $"PurgeQueue on the deleted queue answered {purgeErr}");
    }

    private static async Task DeleteQuietly(AmazonSQSClient sqs, string url)
    {
        try
        {
            await sqs.DeleteQueueAsync(url);
        }
        catch (AmazonServiceException exc) when (exc.ErrorCode == NotFound)
        {
        }
    }
}
