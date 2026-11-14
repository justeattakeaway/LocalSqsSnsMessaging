// Translated from behavioral/sqs/check_tag_queue_merge_preserves_existing_tags.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Diagnostics;
using Amazon.SQS;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sqs;

/// <summary>sqs.tag-queue-merge-preserves-existing-tags</summary>
[Category("AwsEvaluationSuite")]
public sealed class TagQueueMergePreservesExistingTags
{
    /// <summary>
    /// TagQueue merges into the existing tag set (unspecified keys are preserved,
    /// specified keys are overwritten/added); UntagQueue removes only the named keys.
    /// </summary>
    [Test]
    [DisplayName("sqs.tag-queue-merge-preserves-existing-tags")]
    [Timeout(FidelityContext.BudgetMilliseconds)]
    [KnownFidelityGap(FidelityGap.Tags, "tags passed to CreateQueue are dropped: ListQueueTags read {}")]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sqs = ctx.Sqs;
        var url = (await sqs.CreateQueueAsync(new CreateQueueRequest
        {
            QueueName = FidelityContext.UniqueName("sim2real-sqs-tags-"),
            Tags = new() { ["env"] = "test", ["team"] = "alpha" },
        })).QueueUrl;
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(url));

        var wantInitial = new Dictionary<string, string> { ["env"] = "test", ["team"] = "alpha" };
        var initial = await PollTags(sqs, url, wantInitial);
        Ensure(DictEquals(initial, wantInitial), Show(initial));

        await sqs.TagQueueAsync(new TagQueueRequest { QueueUrl = url, Tags = new() { ["team"] = "beta", ["extra"] = "x" } });
        var wantMerged = new Dictionary<string, string> { ["env"] = "test", ["team"] = "beta", ["extra"] = "x" };
        var merged = await PollTags(sqs, url, wantMerged);
        Ensure(DictEquals(merged, wantMerged), Show(merged));

        await sqs.UntagQueueAsync(new UntagQueueRequest { QueueUrl = url, TagKeys = ["extra"] });
        var wantAfterUntag = new Dictionary<string, string> { ["env"] = "test", ["team"] = "beta" };
        var afterUntag = await PollTags(sqs, url, wantAfterUntag);
        Ensure(DictEquals(afterUntag, wantAfterUntag), Show(afterUntag));
    }

    private static bool DictEquals(Dictionary<string, string> a, Dictionary<string, string> b)
        => a.Count == b.Count && a.All(kv => b.TryGetValue(kv.Key, out var v) && v == kv.Value);

    private static string Show(Dictionary<string, string> d) => "{" + string.Join(", ", d.Select(kv => $"'{kv.Key}': '{kv.Value}'")) + "}";

    /// <summary>
    /// ListQueueTags can lag a preceding write; poll up to 10s for it to
    /// settle, returning whatever was last observed so the caller's own assert
    /// still names expected vs actual.
    /// </summary>
    private static async Task<Dictionary<string, string>> PollTags(AmazonSQSClient sqs, string url, Dictionary<string, string> want)
    {
        var deadline = Stopwatch.StartNew();
        Dictionary<string, string> seen = [];
        while (deadline.Elapsed < TimeSpan.FromSeconds(10))
        {
            seen = (await sqs.ListQueueTagsAsync(new ListQueueTagsRequest { QueueUrl = url })).Tags ?? [];
            if (DictEquals(seen, want))
            {
                break;
            }

            await Task.Delay(TimeSpan.FromSeconds(0.5));
        }

        return seen;
    }
}
