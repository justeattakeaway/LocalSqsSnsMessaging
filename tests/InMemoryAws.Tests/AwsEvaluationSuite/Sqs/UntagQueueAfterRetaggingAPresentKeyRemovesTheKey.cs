// Translated from behavioral/sqs/check_untag_queue_after_retagging_a_present_key_removes_the_key.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit abd0811).
using Amazon.Runtime;
using Amazon.SQS;
using Amazon.SQS.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.Sqs;

/// <summary>sqs.untag-queue-after-retagging-a-present-key-removes-the-key</summary>
[Category("AwsEvaluationSuite")]
public sealed class UntagQueueAfterRetaggingAPresentKeyRemovesTheKey
{
    private const string Check = "untag-queue-after-retagging-a-present-key-removes-the-key";
    private const string Gone = "AWS.SimpleQueueService.NonExistentQueue";

    /// <summary>
    /// Re-tagging a key the queue already carries and then untagging it removes the key outright:
    /// the earlier value does not come back.
    /// </summary>
    [Test]
    [DisplayName("sqs.untag-queue-after-retagging-a-present-key-removes-the-key")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sqs = ctx.Sqs;
        var created = new Dictionary<string, string> { ["sim2real"] = Check, ["env"] = "first" };
        var url = (await sqs.CreateQueueAsync(new CreateQueueRequest { QueueName = FidelityContext.UniqueName("sim2real-uqr-"), Tags = new(created) })).QueueUrl;
        ctx.OnCleanup(DropQueue(sqs, url));

        var got = await TagsUntil(ctx, sqs, url, created);
        Ensure(DictEquals(got, created), $"a queue created with tags {Show(created)} lists {Show(got)}");

        await sqs.TagQueueAsync(new TagQueueRequest { QueueUrl = url, Tags = new() { ["env"] = "second" } });
        var replaced = new Dictionary<string, string> { ["sim2real"] = Check, ["env"] = "second" };
        got = await TagsUntil(ctx, sqs, url, replaced);
        Ensure(DictEquals(got, replaced), $"re-tagging env left the queue listing {Show(got)}, expected {Show(replaced)}");

        await sqs.UntagQueueAsync(new UntagQueueRequest { QueueUrl = url, TagKeys = ["env"] });
        var rest = new Dictionary<string, string> { ["sim2real"] = Check };
        got = await TagsUntil(ctx, sqs, url, rest);
        Ensure(DictEquals(got, rest), $"untagging the re-tagged env left {Show(got)}, expected {Show(rest)} with env gone");
    }

    /// <summary>cap_s, shortened when the runner stops the check sooner, keeping 5 s for the failure.</summary>
    private static double WaitCap(FidelityContext ctx, double capSeconds) =>
        Math.Max(1.0, Math.Min(capSeconds, ctx.TimeLeftSeconds - 5.0));

    /// <summary>A cleanup deleting the queue; only the queue's own not-found code counts as already gone.</summary>
    private static Func<Task> DropQueue(AmazonSQSClient sqs, string url) => async () =>
    {
        try
        {
            await sqs.DeleteQueueAsync(url);
        }
        catch (AmazonServiceException exc) when (exc.ErrorCode == Gone)
        {
        }
    };

    /// <summary>ListQueueTags repeated until it lists exactly want or the wait runs out; the last map read.</summary>
    private static async Task<Dictionary<string, string>> TagsUntil(FidelityContext ctx, AmazonSQSClient sqs, string url, Dictionary<string, string> want)
    {
        var deadline = FidelityTime.StartNew();
        var cap = TimeSpan.FromSeconds(WaitCap(ctx, 60.0));
        while (true)
        {
            var got = (await sqs.ListQueueTagsAsync(new ListQueueTagsRequest { QueueUrl = url })).Tags ?? [];
            if (DictEquals(got, want) || deadline.Elapsed >= cap)
            {
                return got;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(2));
        }
    }

    private static bool DictEquals(Dictionary<string, string> a, Dictionary<string, string> b)
        => a.Count == b.Count && a.All(kv => b.TryGetValue(kv.Key, out var v) && v == kv.Value);

    private static string Show(Dictionary<string, string> d) => "{" + string.Join(", ", d.Select(kv => $"'{kv.Key}': '{kv.Value}'")) + "}";
}
