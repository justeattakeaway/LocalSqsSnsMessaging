// Translated from behavioral/sns/check_topic_tags_at_create_overwrite_and_untag_absent_key.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.SimpleNotificationService;
using Amazon.SimpleNotificationService.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sns;

/// <summary>sns.topic-tags-at-create-overwrite-and-untag-absent-key</summary>
[Category("AwsEvaluationSuite")]
public sealed class TopicTagsAtCreateOverwriteAndUntagAbsentKey
{
    private const string Check = "topic-tags-at-create-overwrite-and-untag-absent-key";

    private static async Task<Dictionary<string, string>> Tags(AmazonSimpleNotificationServiceClient sns, string arn)
    {
        var listed = (await sns.ListTagsForResourceAsync(new ListTagsForResourceRequest { ResourceArn = arn })).Tags ?? [];
        var keys = listed.Select(t => t.Key).ToList();
        Ensure(keys.Count == keys.Distinct().Count(),
            $"ListTagsForResource returned a key more than once: [{string.Join(", ", keys.Order(StringComparer.Ordinal).Select(k => $"'{k}'"))}]");
        return listed.ToDictionary(t => t.Key, t => t.Value);
    }

    private static bool Same(Dictionary<string, string> actual, Dictionary<string, string> expected) =>
        actual.Count == expected.Count && expected.All(kv => actual.TryGetValue(kv.Key, out var v) && v == kv.Value);

    private static string Show(Dictionary<string, string> d) => "{" + string.Join(", ", d.Select(kv => $"'{kv.Key}': '{kv.Value}'")) + "}";

    /// <summary>
    /// CreateTopic tags are stored, TagResource on an existing key replaces its value in place, an
    /// empty value is legal, and UntagResource naming an absent key removes only the keys that exist.
    /// </summary>
    [Test]
    [DisplayName("sns.topic-tags-at-create-overwrite-and-untag-absent-key")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    [KnownFidelityGap(FidelityGap.SnsTags, "Tags given to CreateTopic are not stored: ListTagsForResource returns none")]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sns = ctx.Sns;
        var arn = (await sns.CreateTopicAsync(new CreateTopicRequest
        {
            Name = FidelityContext.UniqueName("sim2real-behav-sns-tagset-"),
            Tags =
            [
                new() { Key = "sim2real", Value = Check }, new() { Key = "env", Value = "dev" },
                new() { Key = "owner", Value = "team-a" },
            ],
        })).TopicArn;
        ctx.OnCleanup(() => sns.DeleteTopicAsync(new DeleteTopicRequest { TopicArn = arn }));

        Ensure(Same(await Tags(sns, arn), new() { ["sim2real"] = Check, ["env"] = "dev", ["owner"] = "team-a" }),
            $"tags given at CreateTopic must be stored exactly, got {Show(await Tags(sns, arn))}");

        await sns.TagResourceAsync(new TagResourceRequest
        {
            ResourceArn = arn,
            Tags = [new() { Key = "env", Value = "prod" }, new() { Key = "empty", Value = "" }],
        });
        var afterTag = await Tags(sns, arn);
        Ensure(Same(afterTag, new() { ["sim2real"] = Check, ["env"] = "prod", ["owner"] = "team-a", ["empty"] = "" }),
            $"TagResource must overwrite env in place, add empty='' and keep the rest, got {Show(afterTag)}");

        await sns.UntagResourceAsync(new UntagResourceRequest { ResourceArn = arn, TagKeys = ["owner", "never-set"] });
        var afterUntag = await Tags(sns, arn);
        Ensure(Same(afterUntag, new() { ["sim2real"] = Check, ["env"] = "prod", ["empty"] = "" }),
            $"UntagResource must drop owner, ignore the absent key and keep the rest, got {Show(afterUntag)}");
    }
}
