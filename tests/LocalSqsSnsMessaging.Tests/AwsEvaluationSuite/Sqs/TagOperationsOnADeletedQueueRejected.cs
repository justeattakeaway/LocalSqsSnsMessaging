// Translated from behavioral/sqs/check_tag_operations_on_a_deleted_queue_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Diagnostics;
using Amazon.Runtime;
using Amazon.SQS;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sqs;

/// <summary>sqs.tag-operations-on-a-deleted-queue-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class TagOperationsOnADeletedQueueRejected
{
    private const string NoSuchQueue = "AWS.SimpleQueueService.NonExistentQueue";
    private const string NoSuchQueueMsg = "The specified queue does not exist.";
    private const double SettleSeconds = 60.0;

    private static Dictionary<string, string> Tags() => new() { ["sim2real"] = "tag-operations-on-a-deleted-queue-rejected", ["team"] = "blue" };

    /// <summary>
    /// Once a tagged queue is deleted, ListQueueTags, TagQueue and UntagQueue on its URL are
    /// refused with NonExistentQueue and its tags are unreadable; a sibling queue with the same
    /// tags still accepts TagQueue and UntagQueue and lists the result.
    /// </summary>
    [Test]
    [DisplayName("sqs.tag-operations-on-a-deleted-queue-rejected")]
    [Timeout(FidelityContext.BudgetMilliseconds)]
    [KnownFidelityGap(FidelityGap.Tags, "the queue to be deleted did not carry its tags beforehand (CreateQueue drops tags)")]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sqs = ctx.Sqs;
        var @base = FidelityContext.UniqueName("sim2real-behav-deltags-");
        var doomed = (await sqs.CreateQueueAsync(new CreateQueueRequest { QueueName = @base + "-gone", Tags = Tags() })).QueueUrl;
        ctx.OnCleanup(() => DeleteQuietly(sqs, doomed));
        var sibling = (await sqs.CreateQueueAsync(new CreateQueueRequest { QueueName = @base + "-kept", Tags = Tags() })).QueueUrl;
        ctx.OnCleanup(() => DeleteQuietly(sqs, sibling));

        Ensure(DictEquals((await sqs.ListQueueTagsAsync(new ListQueueTagsRequest { QueueUrl = doomed })).Tags ?? [], Tags()),
            "the queue to be deleted did not carry its tags beforehand");
        await sqs.DeleteQueueAsync(doomed);

        // Deletion is eventually consistent; poll until the URL is refused, then hold every call to
        // the exact refusal.
        AwsError? err = null;
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(SettleSeconds))
        {
            err = await FidelityContext.CaptureError(() => sqs.ListQueueTagsAsync(new ListQueueTagsRequest { QueueUrl = doomed }));
            if (err is not null)
            {
                break;
            }

            await Task.Delay(TimeSpan.FromSeconds(3));
        }

        Ensure((err?.Code, err?.Message) == (NoSuchQueue, NoSuchQueueMsg),
            $"ListQueueTags on the deleted queue answered {err?.ToString() ?? "{}"} within {SettleSeconds}s");
        foreach (var (label, fn) in new (string, Func<Task>)[]
        {
            ("TagQueue", () => sqs.TagQueueAsync(new TagQueueRequest { QueueUrl = doomed, Tags = new() { ["late"] = "x" } })),
            ("UntagQueue", () => sqs.UntagQueueAsync(new UntagQueueRequest { QueueUrl = doomed, TagKeys = ["team"] })),
            ("ListQueueTags", () => sqs.ListQueueTagsAsync(new ListQueueTagsRequest { QueueUrl = doomed })),
        })
        {
            var e = await Error(fn);
            Ensure((e.Code, e.Message) == (NoSuchQueue, NoSuchQueueMsg), $"{label} on the deleted queue answered {e}");
        }

        // Positive control: the sibling is a full tag target.
        await sqs.TagQueueAsync(new TagQueueRequest { QueueUrl = sibling, Tags = new() { ["late"] = "x" } });
        await sqs.UntagQueueAsync(new UntagQueueRequest { QueueUrl = sibling, TagKeys = ["team"] });
        var got = (await sqs.ListQueueTagsAsync(new ListQueueTagsRequest { QueueUrl = sibling })).Tags ?? [];
        var want = Tags().Where(kv => kv.Key != "team").ToDictionary(kv => kv.Key, kv => kv.Value);
        want["late"] = "x";
        Ensure(DictEquals(got, want), $"the sibling queue's tags read {Show(got)}, expected {Show(want)}");
    }

    private static bool DictEquals(Dictionary<string, string> a, Dictionary<string, string> b)
        => a.Count == b.Count && a.All(kv => b.TryGetValue(kv.Key, out var v) && v == kv.Value);

    private static string Show(Dictionary<string, string> d) => "{" + string.Join(", ", d.Select(kv => $"'{kv.Key}': '{kv.Value}'")) + "}";

    /// <summary>The botocore Error dict <paramref name="fn"/> raises. An unexpected success raises, naming the result.</summary>
    private static async Task<AwsError> Error(Func<Task> fn)
        => await FidelityContext.CaptureError(fn) ?? throw Fail("expected the call to be refused, but it succeeded");

    private static async Task DeleteQuietly(AmazonSQSClient sqs, string url)
    {
        try
        {
            await sqs.DeleteQueueAsync(url);
        }
        catch (AmazonServiceException exc) when (exc.ErrorCode == NoSuchQueue)
        {
        }
    }
}
