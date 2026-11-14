// Translated from behavioral/sns/check_concurrent_tag_resource_distinct_keys_all_retained.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.SimpleNotificationService;
using Amazon.SimpleNotificationService.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sns;

/// <summary>sns.concurrent-tag-resource-distinct-keys-all-retained</summary>
[Category("AwsEvaluationSuite")]
public sealed class ConcurrentTagResourceDistinctKeysAllRetained
{
    private const string Check = "concurrent-tag-resource-distinct-keys-all-retained";
    private const int Writers = 12;

    /// <summary>
    /// Twelve concurrent TagResource calls on one topic, each adding a distinct key, leave all
    /// twelve keys plus the creation tag: no writer's tag is lost to a read-modify-write race.
    /// </summary>
    [Test]
    [DisplayName("sns.concurrent-tag-resource-distinct-keys-all-retained")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sns = ctx.Sns;
        var arn = (await sns.CreateTopicAsync(new CreateTopicRequest
        {
            Name = FidelityContext.UniqueName("sim2real-behav-sns-cctag-"),
            Tags = [new() { Key = "sim2real", Value = Check }],
        })).TopicArn;
        ctx.OnCleanup(() => sns.DeleteTopicAsync(new DeleteTopicRequest { TopicArn = arn }));

        var wanted = Enumerable.Range(0, Writers).ToDictionary(i => $"k{i:D2}", i => $"v{i:D2}");
        await Task.WhenAll(wanted.Select(kv => Task.Run(() => TagWithRetry(sns, arn, kv.Key, kv.Value))));

        var stored = ((await sns.ListTagsForResourceAsync(new ListTagsForResourceRequest { ResourceArn = arn })).Tags ?? [])
            .ToDictionary(t => t.Key, t => t.Value);
        var missing = wanted.Keys.Except(stored.Keys).Order(StringComparer.Ordinal).ToList();
        Ensure(missing.Count == 0, $"{missing.Count} concurrently written tag(s) were lost: [{string.Join(", ", missing.Select(m => $"'{m}'"))}]");
        var expected = new Dictionary<string, string>(wanted) { ["sim2real"] = Check };
        Ensure(stored.Count == expected.Count && stored.All(kv => expected.TryGetValue(kv.Key, out var v) && v == kv.Value),
            $"the tag set after the concurrent writes must be exactly the 13 keys, got {{{string.Join(", ", stored.Select(kv => $"'{kv.Key}': '{kv.Value}'"))}}}");
    }

    /// <summary>TagResource, retrying only on the documented ConcurrentAccess conflict.</summary>
    private static async Task TagWithRetry(AmazonSimpleNotificationServiceClient sns, string arn, string key, string value, int attempts = 8)
    {
        for (var i = 0; i < attempts; i++)
        {
            var error = await FidelityContext.CaptureError(() =>
                sns.TagResourceAsync(new TagResourceRequest { ResourceArn = arn, Tags = [new() { Key = key, Value = value }] }));
            if (error is null)
            {
                return;
            }

            if (error.Code != "ConcurrentAccess")
            {
                throw error.Exception;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(0.5));
        }

        throw Fail($"TagResource for '{key}' kept conflicting after {attempts} attempts");
    }
}
