// Translated from behavioral/sqs/check_single_message_operations_on_a_deleted_queue_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit abd0811).
using Amazon.Runtime;
using Amazon.SQS;
using Amazon.SQS.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.Sqs;

/// <summary>sqs.single-message-operations-on-a-deleted-queue-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class SingleMessageOperationsOnADeletedQueueRejected
{
    private const string Check = "single-message-operations-on-a-deleted-queue-rejected";
    private const string NotFound = "AWS.SimpleQueueService.NonExistentQueue";
    private const string NotFoundMsg = "The specified queue does not exist.";
    private const double SettleSeconds = 60.0;

    /// <summary>
    /// ChangeMessageVisibility and DeleteMessage with a receipt handle minted before the
    /// queue was deleted are each refused with NonExistentQueue once an independent read already
    /// reports the queue gone, while a sibling queue still accepts both calls on its own handle.
    /// </summary>
    [Test]
    [DisplayName("sqs.single-message-operations-on-a-deleted-queue-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sqs = ctx.Sqs;
        var @base = FidelityContext.UniqueName("sim2real-behav-msgdel-");
        var sibling = (await sqs.CreateQueueAsync(new CreateQueueRequest { QueueName = @base + "-kept", Tags = new() { ["sim2real"] = Check } })).QueueUrl;
        ctx.OnCleanup(() => DeleteQuietly(sqs, sibling));
        var doomed = (await sqs.CreateQueueAsync(new CreateQueueRequest { QueueName = @base + "-gone", Tags = new() { ["sim2real"] = Check } })).QueueUrl;
        ctx.OnCleanup(() => DeleteQuietly(sqs, doomed));

        // Positive control: both calls succeed on a live queue, using a receipt handle minted
        // the same way as the doomed queue's.
        var controlReceipt = await ReceiveOne(sqs, sibling, "control");
        await sqs.ChangeMessageVisibilityAsync(sibling, controlReceipt, 5);
        await sqs.DeleteMessageAsync(sibling, controlReceipt);
        var deadline = FidelityTime.StartNew();
        List<Message>? left = null;
        while (deadline.Elapsed < TimeSpan.FromSeconds(SettleSeconds))
        {
            left = (await sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = sibling, MaxNumberOfMessages = 10, WaitTimeSeconds = 3 })).Messages ?? [];
            if (left.Count == 0)
            {
                break;
            }
        }

        Ensure(left is { Count: 0 },
            $"positive control DeleteMessage left messages behind: [{string.Join(", ", (left ?? []).Select(m => m.Body))}]");

        var doomedReceipt = await ReceiveOne(sqs, doomed, "doomed");
        await sqs.DeleteQueueAsync(doomed);

        // Deletion is eventually consistent; poll an independent read (GetQueueAttributes, not
        // ChangeMessageVisibility/DeleteMessage) until it reports the queue gone, then hold both
        // single-message operations to the same refusal on the handle minted before the delete.
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

        var visErr = await Error(() => sqs.ChangeMessageVisibilityAsync(doomed, doomedReceipt, 5));
        Ensure((visErr.Code, visErr.Message) == (NotFound, NotFoundMsg), $"ChangeMessageVisibility on the deleted queue answered {visErr}");
        var delErr = await Error(() => sqs.DeleteMessageAsync(doomed, doomedReceipt));
        Ensure((delErr.Code, delErr.Message) == (NotFound, NotFoundMsg), $"DeleteMessage on the deleted queue answered {delErr}");
    }

    /// <summary>Send <paramref name="body"/> and return the ReceiptHandle of its delivery.</summary>
    private static async Task<string> ReceiveOne(AmazonSQSClient sqs, string url, string body)
    {
        await sqs.SendMessageAsync(url, body);
        var deadline = FidelityTime.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(SettleSeconds))
        {
            var msgs = (await sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = url, MaxNumberOfMessages = 1, WaitTimeSeconds = 3 })).Messages ?? [];
            foreach (var m in msgs)
            {
                if (m.Body == body)
                {
                    return m.ReceiptHandle;
                }
            }
        }

        throw Fail($"never received the message '{body}' on '{url}' within {SettleSeconds}s");
    }

    /// <summary>The botocore Error dict <paramref name="fn"/> raises. An unexpected success raises, naming the result.</summary>
    private static async Task<AwsError> Error(Func<Task> fn)
        => await FidelityContext.CaptureError(fn) ?? throw Fail("expected the call to be refused, but it succeeded");

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
