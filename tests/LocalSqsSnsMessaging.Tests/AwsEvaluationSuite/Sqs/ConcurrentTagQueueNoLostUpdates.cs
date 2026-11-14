// Translated from behavioral/sqs/check_concurrent_tag_queue_no_lost_updates.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sqs;

/// <summary>sqs.concurrent-tag-queue-no-lost-updates</summary>
[Category("AwsEvaluationSuite")]
public sealed class ConcurrentTagQueueNoLostUpdates
{
    /// <summary>
    /// TagQueue merges tags rather than replacing the whole tag set. Firing concurrent
    /// TagQueue calls that each add a disjoint tag key must all persist -- ListQueueTags
    /// afterward shows every key with no lost updates.
    /// </summary>
    [Test]
    [DisplayName("sqs.concurrent-tag-queue-no-lost-updates")]
    [Timeout(FidelityContext.BudgetMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sqs = ctx.Sqs;
        var url = (await sqs.CreateQueueAsync(new CreateQueueRequest { QueueName = FidelityContext.UniqueName("sim2real-behav-ctag-") })).QueueUrl;
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(url));

        var tagPairs = Enumerable.Range(0, 8).ToDictionary(i => $"racer-{i}", i => $"value-{i}");

        async Task<string> Tag(KeyValuePair<string, string> item)
        {
            await sqs.TagQueueAsync(new TagQueueRequest { QueueUrl = url, Tags = new() { [item.Key] = item.Value } });
            return item.Key;
        }

        var doneKeys = await Task.WhenAll(tagPairs.Select(item => Task.Run(() => Tag(item))));
        Ensure(doneKeys.ToHashSet().SetEquals(tagPairs.Keys),
            $"expected TagQueue to complete for every key [{string.Join(", ", tagPairs.Keys.Order(StringComparer.Ordinal))}], " +
            $"got [{string.Join(", ", doneKeys.Order(StringComparer.Ordinal))}]");

        var finalTags = (await sqs.ListQueueTagsAsync(new ListQueueTagsRequest { QueueUrl = url })).Tags ?? [];
        foreach (var (key, value) in tagPairs)
        {
            Ensure(finalTags.ContainsKey(key), $"tag '{key}' lost under concurrent TagQueue calls");
            Ensure(finalTags[key] == value, $"tag '{key}' has wrong value '{finalTags[key]}'");
        }

        Ensure(finalTags.Count == tagPairs.Count,
            $"expected exactly {tagPairs.Count} tags, found {finalTags.Count}: {{{string.Join(", ", finalTags.Select(kv => $"'{kv.Key}': '{kv.Value}'"))}}}");
    }
}
