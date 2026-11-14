// Translated from behavioral/sns/check_tag_resource_lifecycle.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.SimpleNotificationService;
using Amazon.SimpleNotificationService.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sns;

/// <summary>sns.tag-resource-lifecycle</summary>
[Category("AwsEvaluationSuite")]
public sealed class TagResourceLifecycle
{
    /// <summary>TagResource adds tags visible via ListTagsForResource; UntagResource removes them.</summary>
    [Test]
    [DisplayName("sns.tag-resource-lifecycle")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ev = ctx.Sns;
        var topicArn = await CreateTopic(ev, ctx, FidelityContext.UniqueName("sim2real-behav-sns-tags-"));
        await ev.TagResourceAsync(new TagResourceRequest { ResourceArn = topicArn, Tags = [new() { Key = "env", Value = "fidelity" }] });
        var tags = await Tags(ev, topicArn);
        Ensure(tags.GetValueOrDefault("env") == "fidelity", $"expected tag env=fidelity, got {Show(tags)}");
        await ev.UntagResourceAsync(new UntagResourceRequest { ResourceArn = topicArn, TagKeys = ["env"] });
        var tagsAfter = await Tags(ev, topicArn);
        Ensure(!tagsAfter.ContainsKey("env"), $"expected tag 'env' removed, still present: {Show(tagsAfter)}");
    }

    private static async Task<Dictionary<string, string>> Tags(AmazonSimpleNotificationServiceClient ev, string arn) =>
        ((await ev.ListTagsForResourceAsync(new ListTagsForResourceRequest { ResourceArn = arn })).Tags ?? []).ToDictionary(t => t.Key, t => t.Value);

    private static string Show(Dictionary<string, string> d) => $"{{{string.Join(", ", d.Select(kv => $"'{kv.Key}': '{kv.Value}'"))}}}";

    /// <summary>CreateTopic + register best-effort DeleteTopic cleanup; returns the TopicArn.</summary>
    private static async Task<string> CreateTopic(AmazonSimpleNotificationServiceClient client, FidelityContext ctx, string name,
        Dictionary<string, string>? attributes = null, List<Amazon.SimpleNotificationService.Model.Tag>? tags = null)
    {
        var request = new CreateTopicRequest { Name = name };
        if (attributes is { Count: > 0 })
        {
            request.Attributes = attributes;
        }

        if (tags is { Count: > 0 })
        {
            request.Tags = tags;
        }

        var arn = (await client.CreateTopicAsync(request)).TopicArn;
        ctx.OnCleanup(() => client.DeleteTopicAsync(new DeleteTopicRequest { TopicArn = arn }));
        return arn;
    }
}
