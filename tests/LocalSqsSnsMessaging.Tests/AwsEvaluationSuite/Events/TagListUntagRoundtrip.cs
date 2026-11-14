// Translated from behavioral/events/check_tag_list_untag_roundtrip.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.EventBridge.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Events;

/// <summary>events.tag-list-untag-roundtrip</summary>
[Category("AwsEvaluationSuite")]
public sealed class TagListUntagRoundtrip
{
    /// <summary>TagResource/UntagResource round-trip is reflected by ListTagsForResource.</summary>
    [Test]
    [DisplayName("events.tag-list-untag-roundtrip")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ev = ctx.Events;
        var bus = FidelityContext.UniqueName("sim2real-behav-bus-");
        var resp = await ev.CreateEventBusAsync(new CreateEventBusRequest { Name = bus });
        ctx.OnCleanup(() => ev.DeleteEventBusAsync(new DeleteEventBusRequest { Name = bus }));
        var arn = resp.EventBusArn;
        await ev.TagResourceAsync(new TagResourceRequest { ResourceARN = arn, Tags = [new Tag { Key = "env", Value = "test" }] });
        var listed = await ev.ListTagsForResourceAsync(new ListTagsForResourceRequest { ResourceARN = arn });
        var tags = (listed.Tags ?? []).ToDictionary(t => t.Key, t => t.Value);
        Ensure(tags.GetValueOrDefault("env") == "test", $"expected tag env=test after TagResource, got {Show(tags)}");
        await ev.UntagResourceAsync(new UntagResourceRequest { ResourceARN = arn, TagKeys = ["env"] });
        var listed2 = await ev.ListTagsForResourceAsync(new ListTagsForResourceRequest { ResourceARN = arn });
        var tags2 = (listed2.Tags ?? []).ToDictionary(t => t.Key, t => t.Value);
        Ensure(!tags2.ContainsKey("env"), $"expected tag env removed after UntagResource, still present in {Show(tags2)}");
    }

    private static string Show(Dictionary<string, string> d) => "{" + string.Join(", ", d.Select(kv => $"'{kv.Key}': '{kv.Value}'")) + "}";
}
