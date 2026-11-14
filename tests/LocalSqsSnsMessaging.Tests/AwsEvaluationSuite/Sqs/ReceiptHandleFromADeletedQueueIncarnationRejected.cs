// Translated from behavioral/sqs/check_receipt_handle_from_a_deleted_queue_incarnation_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Diagnostics;
using Amazon.SQS;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sqs;

/// <summary>sqs.receipt-handle-from-a-deleted-queue-incarnation-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class ReceiptHandleFromADeletedQueueIncarnationRejected
{
    private const string NoSuchQueue = "AWS.SimpleQueueService.NonExistentQueue";
    private const string NoSuchQueueMsg = "The specified queue does not exist.";
    private const string Recent = "AWS.SimpleQueueService.QueueDeletedRecently";
    private const double RecreateDeadlineS = 150.0;

    private static Dictionary<string, string> OldTags() => new()
    {
        ["sim2real"] = "receipt-handle-from-a-deleted-queue-incarnation-rejected",
        ["generation"] = "old",
    };

    private static Dictionary<string, string> NewTags() => new()
    {
        ["sim2real"] = "receipt-handle-from-a-deleted-queue-incarnation-rejected",
    };

    /// <summary>
    /// A receipt handle from a deleted queue is refused with NonExistentQueue by DeleteMessage and
    /// ChangeMessageVisibility on a same-name recreated queue, which keeps none of the old tags or
    /// messages, while the new queue's own message and handle behave normally.
    /// </summary>
    [Test]
    [DisplayName("sqs.receipt-handle-from-a-deleted-queue-incarnation-rejected")]
    [Timeout(FidelityContext.BudgetMilliseconds)]
    [KnownFidelityGap(FidelityGap.ReceiptHandles, "DeleteMessage on a recreated queue with the old incarnation's handle is not refused with NonExistentQueue")]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sqs = ctx.Sqs;
        var name = FidelityContext.UniqueName("sim2real-behav-incarn-");
        var url = (await sqs.CreateQueueAsync(new CreateQueueRequest { QueueName = name, Tags = OldTags() })).QueueUrl;
        ctx.OnCleanup(() => DeleteQuietly(sqs, url));
        await sqs.SendMessageAsync(new SendMessageRequest { QueueUrl = url, MessageBody = "old-generation" });
        var old = await ReceiveOne(sqs, url, 300);
        Ensure(old.Body == "old-generation", $"received '{old.Body}'");
        var oldHandle = old.ReceiptHandle;

        await sqs.DeleteQueueAsync(url);
        string? newUrl = null;
        AwsError? last = null;
        var deadline = Stopwatch.StartNew();
        while (newUrl is null && deadline.Elapsed < TimeSpan.FromSeconds(RecreateDeadlineS))
        {
            var (resp, error) = await FidelityContext.Capture(() => sqs.CreateQueueAsync(new CreateQueueRequest { QueueName = name, Tags = NewTags() }));
            if (error is null)
            {
                newUrl = resp!.QueueUrl;
                ctx.OnCleanup(() => DeleteQuietly(sqs, newUrl));
            }
            else
            {
                last = error;
                Ensure(last.Code == Recent, $"recreating the name answered {last}");
                await Task.Delay(TimeSpan.FromSeconds(5));
            }
        }

        Ensure(newUrl is not null,
            $"the name was still refused {RecreateDeadlineS}s after the delete: {last}");
        Ensure(newUrl == url, "the recreated queue has a different URL than the deleted one");

        foreach (var (label, fn) in new (string, Func<Task<object>>)[]
                 {
                     ("DeleteMessage", async () => await sqs.DeleteMessageAsync(new DeleteMessageRequest { QueueUrl = newUrl, ReceiptHandle = oldHandle })),
                     ("ChangeMessageVisibility", async () => await sqs.ChangeMessageVisibilityAsync(new ChangeMessageVisibilityRequest
                     {
                         QueueUrl = newUrl,
                         ReceiptHandle = oldHandle,
                         VisibilityTimeout = 0,
                     })),
                 })
        {
            var err = await Error(fn);
            Ensure((err.Code, err.Message) == (NoSuchQueue, NoSuchQueueMsg),
                $"{label} on the recreated queue with the old incarnation's handle answered {err}");
        }

        var gotTags = (await sqs.ListQueueTagsAsync(new ListQueueTagsRequest { QueueUrl = newUrl })).Tags ?? [];
        var newTags = NewTags();
        Ensure(gotTags.Count == newTags.Count && !gotTags.Except(newTags).Any(),
            $"the recreated queue carries tags {string.Join(", ", gotTags)}");

        await sqs.SendMessageAsync(new SendMessageRequest { QueueUrl = newUrl, MessageBody = "new-generation" });
        var err2 = await Error(async () => await sqs.DeleteMessageAsync(new DeleteMessageRequest { QueueUrl = newUrl, ReceiptHandle = oldHandle }));
        Ensure((err2.Code, err2.Message) == (NoSuchQueue, NoSuchQueueMsg),
            $"with a message on the recreated queue, the old handle's DeleteMessage answered {err2}");

        // Positive control: the new queue's own message and handle behave normally, and the old
        // message is not there.
        var fresh = await ReceiveOne(sqs, newUrl, 60);
        Ensure(fresh.Body == "new-generation",
            $"the recreated queue delivered '{fresh.Body}'; the old message must not survive");
        await sqs.ChangeMessageVisibilityAsync(new ChangeMessageVisibilityRequest
        {
            QueueUrl = newUrl,
            ReceiptHandle = fresh.ReceiptHandle,
            VisibilityTimeout = 0,
        });
        var again = await ReceiveOne(sqs, newUrl, 60);
        Ensure(again.Body == "new-generation", $"after release the queue delivered '{again.Body}'");
        await sqs.DeleteMessageAsync(new DeleteMessageRequest { QueueUrl = newUrl, ReceiptHandle = again.ReceiptHandle });
        var rest = await sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = newUrl, MaxNumberOfMessages = 10, WaitTimeSeconds = 3 });
        Ensure((rest.Messages ?? []).Count == 0,
            "after deleting its one message the recreated queue still delivered "
            + $"[{string.Join(", ", (rest.Messages ?? []).Select(m => m.Body))}]");
    }

    /// <summary>The botocore Error dict <paramref name="fn"/> raises. An unexpected success raises, naming the result.</summary>
    private static async Task<AwsError> Error(Func<Task<object>> fn)
    {
        var (result, error) = await FidelityContext.Capture(fn);
        return error ?? throw Fail($"expected the call to be refused, but it succeeded: {result}");
    }

    private static async Task DeleteQuietly(IAmazonSQS sqs, string url)
    {
        var err = await FidelityContext.CaptureError(() => sqs.DeleteQueueAsync(new DeleteQueueRequest { QueueUrl = url }));
        if (err is not null && err.Code != NoSuchQueue)
        {
            throw err.Exception;
        }
    }

    private static async Task<Message> ReceiveOne(IAmazonSQS sqs, string url, int visibility)
    {
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(30))
        {
            var msgs = (await sqs.ReceiveMessageAsync(new ReceiveMessageRequest
            {
                QueueUrl = url,
                WaitTimeSeconds = 3,
                VisibilityTimeout = visibility,
            })).Messages ?? [];
            if (msgs.Count > 0)
            {
                return msgs[0];
            }
        }

        throw Fail("no message was received from the queue within 30s");
    }
}
