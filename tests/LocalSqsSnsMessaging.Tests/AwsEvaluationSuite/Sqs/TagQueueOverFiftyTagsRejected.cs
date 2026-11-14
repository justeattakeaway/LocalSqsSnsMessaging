// Translated from behavioral/sqs/check_tag_queue_over_fifty_tags_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.SQS;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sqs;

/// <summary>sqs.tag-queue-over-fifty-tags-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class TagQueueOverFiftyTagsRejected
{
    private const string Check = "tag-queue-over-fifty-tags-rejected";

    private const int Limit = 50;

    /// <summary>
    /// A queue's tag set is capped at 50 across calls: 51 at once or a 51st is refused with nothing
    /// applied, untagging an absent key is a no-op, and removing one key makes room for another.
    /// </summary>
    [Test]
    [DisplayName("sqs.tag-queue-over-fifty-tags-rejected")]
    [Timeout(FidelityContext.BudgetMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sqs = ctx.Sqs;
        var name = FidelityContext.UniqueName("sim2real-behav-t50-");
        var url = (await sqs.CreateQueueAsync(new CreateQueueRequest { QueueName = name })).QueueUrl;
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(url));
        var msgOver = $"Too many tags added for queue {name}.";

        var (code, msg) = await Err(() => sqs.TagQueueAsync(new TagQueueRequest
        {
            QueueUrl = url,
            Tags = Enumerable.Range(0, Limit + 1).ToDictionary(i => $"k{i}", _ => "v"),
        }));
        Ensure((code, msg) == ("InvalidParameterValue", msgOver), $"51 tags in one call gave ({code}, {msg})");
        Ensure((await GetTags(sqs, url)).Count == 0, $"the refused 51-tag call stored {(await GetTags(sqs, url)).Count} tags");

        var full = Enumerable.Range(0, Limit).ToDictionary(i => $"k{i}", _ => "v");
        full["sim2real"] = Check;
        full.Remove("k0");
        await sqs.TagQueueAsync(new TagQueueRequest { QueueUrl = url, Tags = full });
        Ensure(DictEquals(await GetTags(sqs, url), full),
            $"exactly 50 tags were accepted but {(await GetTags(sqs, url)).Count} read back");

        (code, msg) = await Err(() => sqs.TagQueueAsync(new TagQueueRequest { QueueUrl = url, Tags = new() { ["extra"] = "v" } }));
        Ensure((code, msg) == ("InvalidParameterValue", msgOver), $"a 51st tag on a full queue gave ({code}, {msg})");
        var after = await GetTags(sqs, url);
        Ensure(DictEquals(after, full),
            $"the refused 51st tag changed the tag set: {after.Count} tags, " +
            $"'extra' {(after.ContainsKey("extra") ? "present" : "absent")}");

        await sqs.UntagQueueAsync(new UntagQueueRequest { QueueUrl = url, TagKeys = ["sim2real-not-a-key"] });
        Ensure(DictEquals(await GetTags(sqs, url), full), "untagging an absent key changed the tag set");

        await sqs.UntagQueueAsync(new UntagQueueRequest { QueueUrl = url, TagKeys = ["k1"] });
        await sqs.TagQueueAsync(new TagQueueRequest { QueueUrl = url, Tags = new() { ["extra"] = "v" } });
        var expected = full.Where(kv => kv.Key != "k1").ToDictionary(kv => kv.Key, kv => kv.Value);
        expected["extra"] = "v";
        var final = await GetTags(sqs, url);
        Ensure(DictEquals(final, expected),
            $"after freeing one slot the new tag did not fit: {final.Count} tags, 'extra' " +
            $"{(final.ContainsKey("extra") ? "present" : "absent")}, 'k1' " +
            $"{(final.ContainsKey("k1") ? "present" : "absent")}");
    }

    private static bool DictEquals(Dictionary<string, string> a, Dictionary<string, string> b)
        => a.Count == b.Count && a.All(kv => b.TryGetValue(kv.Key, out var v) && v == kv.Value);

    /// <summary>(Code, Message) of the ClientError <paramref name="fn"/> raises; an unexpected success raises.</summary>
    private static async Task<(string? Code, string? Message)> Err(Func<Task<TagQueueResponse>> fn)
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is not null)
        {
            return (error.Code, error.Message);
        }

        throw Fail($"expected the call to be refused, but it succeeded: {{'HTTPStatusCode': {(int)result!.HttpStatusCode}}}");
    }

    private static async Task<Dictionary<string, string>> GetTags(AmazonSQSClient sqs, string url)
        => (await sqs.ListQueueTagsAsync(new ListQueueTagsRequest { QueueUrl = url })).Tags ?? [];
}
