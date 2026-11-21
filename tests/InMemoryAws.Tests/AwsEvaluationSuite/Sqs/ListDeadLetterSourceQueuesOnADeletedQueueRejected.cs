// Translated from behavioral/sqs/check_list_dead_letter_source_queues_on_a_deleted_queue_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit abd0811).
using System.Text.Json;
using Amazon.Runtime;
using Amazon.SQS;
using Amazon.SQS.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.Sqs;

/// <summary>sqs.list-dead-letter-source-queues-on-a-deleted-queue-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class ListDeadLetterSourceQueuesOnADeletedQueueRejected
{
    private const string Check = "list-dead-letter-source-queues-on-a-deleted-queue-rejected";
    private const string NotFound = "AWS.SimpleQueueService.NonExistentQueue";
    private const string NotFoundMsg = "The specified queue does not exist.";
    private const double SettleSeconds = 60.0;

    /// <summary>
    /// ListDeadLetterSourceQueues on a dead-letter queue deleted earlier in the check is
    /// refused with NonExistentQueue after an independent read already reports it gone, while a
    /// sibling dead-letter queue still lists the source wired to it.
    /// </summary>
    [Test]
    [DisplayName("sqs.list-dead-letter-source-queues-on-a-deleted-queue-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sqs = ctx.Sqs;
        var @base = FidelityContext.UniqueName("sim2real-behav-dlqlist-");

        async Task<string> Create(string suffix)
        {
            var url = (await sqs.CreateQueueAsync(new CreateQueueRequest { QueueName = @base + suffix, Tags = new() { ["sim2real"] = Check } })).QueueUrl;
            ctx.OnCleanup(() => DeleteQuietly(sqs, url));
            return url;
        }

        var doomedDlq = await Create("-dlq-gone");
        var srcForDoomed = await Create("-src-gone");
        var keptDlq = await Create("-dlq-kept");
        var srcForKept = await Create("-src-kept");

        async Task Wire(string srcUrl, string dlqUrl)
        {
            var dlqArn = (await sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest { QueueUrl = dlqUrl, AttributeNames = ["QueueArn"] })).Attributes!["QueueArn"];
            await sqs.SetQueueAttributesAsync(new SetQueueAttributesRequest
            {
                QueueUrl = srcUrl,
                Attributes = new() { ["RedrivePolicy"] = JsonSerializer.Serialize(new { deadLetterTargetArn = dlqArn, maxReceiveCount = 5 }) },
            });
        }

        await Wire(srcForDoomed, doomedDlq);
        await Wire(srcForKept, keptDlq);

        // Positive control: the kept dead-letter queue's sources list the source wired to it.
        var deadline = FidelityTime.StartNew();
        List<string> sources = [];
        while (deadline.Elapsed < TimeSpan.FromSeconds(SettleSeconds))
        {
            sources = (await sqs.ListDeadLetterSourceQueuesAsync(new ListDeadLetterSourceQueuesRequest { QueueUrl = keptDlq })).QueueUrls ?? [];
            if (sources.Contains(srcForKept))
            {
                break;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(3));
        }

        Ensure(sources.Contains(srcForKept),
            $"kept dead-letter queue's sources read [{string.Join(", ", sources)}] within {SettleSeconds}s, expected to include '{srcForKept}'");

        await sqs.DeleteQueueAsync(doomedDlq);

        // Deletion is eventually consistent; poll an independent read (GetQueueAttributes, not
        // ListDeadLetterSourceQueues) until it reports the queue gone, then hold
        // ListDeadLetterSourceQueues to the same refusal.
        AwsError? readErr = null;
        deadline = FidelityTime.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(SettleSeconds))
        {
            readErr = await FidelityContext.CaptureError(() => sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest { QueueUrl = doomedDlq, AttributeNames = ["QueueArn"] }));
            if (readErr is not null)
            {
                break;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(3));
        }

        Ensure((readErr?.Code, readErr?.Message) == (NotFound, NotFoundMsg),
            $"GetQueueAttributes on the deleted dead-letter queue answered {readErr?.ToString() ?? "{}"} within {SettleSeconds}s");

        var listErr = await FidelityContext.CaptureError(() => sqs.ListDeadLetterSourceQueuesAsync(new ListDeadLetterSourceQueuesRequest { QueueUrl = doomedDlq }))
            ?? throw Fail("expected the call to be refused, but it succeeded");
        Ensure((listErr.Code, listErr.Message) == (NotFound, NotFoundMsg),
            $"ListDeadLetterSourceQueues on the deleted dead-letter queue answered {listErr}");
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
