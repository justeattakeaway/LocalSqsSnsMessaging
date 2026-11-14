// Translated from behavioral/sqs/check_tag_queue_roundtrip.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sqs;

/// <summary>sqs.tag-queue-roundtrip</summary>
[Category("AwsEvaluationSuite")]
public sealed class TagQueueRoundtrip
{
    /// <summary>
    /// TagQueue / ListQueueTags / UntagQueue round-trip.
    ///
    /// Tag reads can lag the write; poll before asserting.
    /// </summary>
    [Test]
    [DisplayName("sqs.tag-queue-roundtrip")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sqs = ctx.Sqs;
        var url = (await sqs.CreateQueueAsync(new CreateQueueRequest { QueueName = FidelityContext.UniqueName("sim2real-behav-") })).QueueUrl;
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(url));
        var token = Guid.NewGuid().ToString("N")[..8];
        await sqs.TagQueueAsync(new TagQueueRequest { QueueUrl = url, Tags = new() { ["env"] = $"test-{token}", ["team"] = "sim2real" } });
        Dictionary<string, string> tagsNow = [];
        var roundTripped = false;
        for (var i = 0; i < 10; i++)
        {
            tagsNow = (await sqs.ListQueueTagsAsync(new ListQueueTagsRequest { QueueUrl = url })).Tags ?? [];
            if (tagsNow.GetValueOrDefault("env") == $"test-{token}" && tagsNow.GetValueOrDefault("team") == "sim2real")
            {
                roundTripped = true;
                break;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(0.5));
        }

        if (!roundTripped)
        {
            throw Fail($"tags did not round-trip: {Show(tagsNow)}");
        }

        await sqs.UntagQueueAsync(new UntagQueueRequest { QueueUrl = url, TagKeys = ["team"] });
        Dictionary<string, string> tags2 = [];
        for (var i = 0; i < 10; i++)
        {
            tags2 = (await sqs.ListQueueTagsAsync(new ListQueueTagsRequest { QueueUrl = url })).Tags ?? [];
            if (!tags2.ContainsKey("team") && tags2.GetValueOrDefault("env") == $"test-{token}")
            {
                return;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(0.5));
        }

        throw Fail($"untag did not remove only 'team': {Show(tags2)}");
    }

    private static string Show(Dictionary<string, string> d) => "{" + string.Join(", ", d.Select(kv => $"'{kv.Key}': '{kv.Value}'")) + "}";
}
