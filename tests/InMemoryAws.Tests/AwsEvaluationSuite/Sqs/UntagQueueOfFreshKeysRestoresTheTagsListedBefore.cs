// Translated from behavioral/sqs/check_untag_queue_of_fresh_keys_restores_the_tags_listed_before.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit abd0811).
using Amazon.Runtime;
using Amazon.SQS;
using Amazon.SQS.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.Sqs;

/// <summary>sqs.untag-queue-of-fresh-keys-restores-the-tags-listed-before</summary>
[Category("AwsEvaluationSuite")]
public sealed class UntagQueueOfFreshKeysRestoresTheTagsListedBefore
{
    private const string Check = "untag-queue-of-fresh-keys-restores-the-tags-listed-before";
    private const string Gone = "AWS.SimpleQueueService.NonExistentQueue";

    /// <summary>
    /// TagQueue of two keys the queue did not carry, then UntagQueue of those keys, leaves
    /// ListQueueTags with exactly the tags it listed before, creation tags included.
    /// </summary>
    [Test]
    [DisplayName("sqs.untag-queue-of-fresh-keys-restores-the-tags-listed-before")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sqs = ctx.Sqs;
        var created = new Dictionary<string, string> { ["sim2real"] = Check, ["owner"] = "lab" };
        var url = (await sqs.CreateQueueAsync(new CreateQueueRequest { QueueName = FidelityContext.UniqueName("sim2real-uqf-"), Tags = new(created) })).QueueUrl;
        ctx.OnCleanup(DropQueue(sqs, url));

        var before = await TagsUntil(ctx, sqs, url, created);
        Ensure(DictEquals(before, created), $"a queue created with tags {Show(created)} lists {Show(before)}");

        var fresh = new Dictionary<string, string> { ["extra-a"] = "one", ["extra-b"] = "two" };
        var freshKeys = "[" + string.Join(", ", fresh.Keys.Order(StringComparer.Ordinal).Select(k => $"'{k}'")) + "]";
        await sqs.TagQueueAsync(new TagQueueRequest { QueueUrl = url, Tags = new(fresh) });
        var tagged = new Dictionary<string, string>(before);
        foreach (var (k, v) in fresh)
        {
            tagged[k] = v;
        }

        var got = await TagsUntil(ctx, sqs, url, tagged);
        Ensure(DictEquals(got, tagged), $"after tagging {freshKeys} the queue lists {Show(got)}, expected {Show(tagged)}");

        await sqs.UntagQueueAsync(new UntagQueueRequest { QueueUrl = url, TagKeys = [.. fresh.Keys.Order(StringComparer.Ordinal)] });
        got = await TagsUntil(ctx, sqs, url, before);
        Ensure(DictEquals(got, before), $"untagging {freshKeys} left {Show(got)}, expected the earlier set {Show(before)}");
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
