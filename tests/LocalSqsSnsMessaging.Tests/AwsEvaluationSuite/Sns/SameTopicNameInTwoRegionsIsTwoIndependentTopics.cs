// Translated from behavioral/sns/check_same_topic_name_in_two_regions_is_two_independent_topics.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.SimpleNotificationService;
using Amazon.SimpleNotificationService.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sns;

/// <summary>sns.same-topic-name-in-two-regions-is-two-independent-topics</summary>
[Category("AwsEvaluationSuite")]
public sealed class SameTopicNameInTwoRegionsIsTwoIndependentTopics
{
    private const string Check = "same-topic-name-in-two-regions-is-two-independent-topics";

    /// <summary>A second commercial region of the same account (region-pinned by design).</summary>
    private static string OtherRegion(string region) => region != "us-east-1" ? "us-east-1" : "us-west-2";

    /// <summary>(Code, Message) of the error fn raises; an unexpected success raises, naming it.</summary>
    private static async Task<(string? Code, string? Message)> Error(Func<Task<GetTopicAttributesResponse>> fn)
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is null)
        {
            throw Fail($"expected the call to be refused, but it succeeded: {{'Attributes': {Show(result!.Attributes ?? [])}}}");
        }

        return (error.Code, error.Message);
    }

    private static string Show(Dictionary<string, string> d) => "{" + string.Join(", ", d.Select(kv => $"'{kv.Key}': '{kv.Value}'")) + "}";

    private static async Task<bool> Listed(AmazonSimpleNotificationServiceClient sns, string arn)
    {
        await foreach (var p in sns.Paginators.ListTopics(new ListTopicsRequest()).Responses)
        {
            if ((p.Topics ?? []).Any(t => t.TopicArn == arn))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// One topic name in two regions yields two independent topics whose ARNs differ only in the
    /// region: attributes, tags and listings stay separate, and deleting one leaves the other.
    /// </summary>
    [Test]
    [DisplayName("sns.same-topic-name-in-two-regions-is-two-independent-topics")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    [KnownFidelityGap(FidelityGap.SnsRegions, "CreateTopic through a us-east-1 client returns the us-west-2 ARN: both regions share one topic")]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var home = ctx.Region;
        var away = OtherRegion(home);
        var sns = ctx.Sns;
        var snsAway = ctx.SnsClient(away);
        var name = FidelityContext.UniqueName("sim2real-behav-sns-twin-");

        var a = (await sns.CreateTopicAsync(new CreateTopicRequest
        {
            Name = name,
            Attributes = new() { ["DisplayName"] = "home twin" },
            Tags = [new() { Key = "sim2real", Value = Check }],
        })).TopicArn;
        ctx.OnCleanup(() => sns.DeleteTopicAsync(new DeleteTopicRequest { TopicArn = a }));
        var b = (await snsAway.CreateTopicAsync(new CreateTopicRequest
        {
            Name = name,
            Attributes = new() { ["DisplayName"] = "away twin" },
            Tags = [new() { Key = "sim2real", Value = Check }],
        })).TopicArn;
        ctx.OnCleanup(() => snsAway.DeleteTopicAsync(new DeleteTopicRequest { TopicArn = b }));

        string[] pa = a.Split(':'), pb = b.Split(':');
        Ensure(pa[3] == home && pb[3] == away, $"the twins' ARN regions are '{pa[3]}' and '{pb[3]}'");
        Ensure(pa[..3].Concat(pa[4..]).SequenceEqual(pb[..3].Concat(pb[4..])), "the twins' ARNs must differ only in the region field");

        await sns.SetTopicAttributesAsync(new SetTopicAttributesRequest { TopicArn = a, AttributeName = "DisplayName", AttributeValue = "home changed" });
        await sns.TagResourceAsync(new TagResourceRequest { ResourceArn = a, Tags = [new() { Key = "side", Value = "home" }] });
        var da = (await sns.GetTopicAttributesAsync(new GetTopicAttributesRequest { TopicArn = a })).Attributes?.GetValueOrDefault("DisplayName");
        var db = (await snsAway.GetTopicAttributesAsync(new GetTopicAttributesRequest { TopicArn = b })).Attributes?.GetValueOrDefault("DisplayName");
        Ensure(da == "home changed" && db == "away twin",
            $"each twin must keep its own DisplayName; got home '{da}', away '{db}'");
        var awayTags = ((await snsAway.ListTagsForResourceAsync(new ListTagsForResourceRequest { ResourceArn = b })).Tags ?? []).Select(t => t.Key).ToHashSet();
        Ensure(awayTags.SetEquals(["sim2real"]),
            $"a tag added to the home twin appeared on the away twin: [{string.Join(", ", awayTags.Order(StringComparer.Ordinal).Select(k => $"'{k}'"))}]");

        Ensure(await Listed(sns, a) && !await Listed(sns, b),
            "the home region must list its own twin and not the other region's");
        Ensure(await Listed(snsAway, b) && !await Listed(snsAway, a),
            "the away region must list its own twin and not the other region's");

        await sns.DeleteTopicAsync(new DeleteTopicRequest { TopicArn = a });
        // Topic deletion is eventually consistent.
        var deadline = FidelityTime.StartNew();
        string? code;
        while (true)
        {
            var error = await FidelityContext.CaptureError(() => sns.GetTopicAttributesAsync(new GetTopicAttributesRequest { TopicArn = a }));
            if (error is not null)
            {
                code = error.Code;
                break;
            }

            Ensure(deadline.Elapsed < TimeSpan.FromSeconds(60), "the deleted home twin is still readable");
            await FidelityTime.Delay(TimeSpan.FromSeconds(2));
        }

        Ensure(code == "NotFound", $"reading the deleted home twin gave '{code}'");
        var survivor = (await snsAway.GetTopicAttributesAsync(new GetTopicAttributesRequest { TopicArn = b })).Attributes ?? [];
        Ensure(survivor.GetValueOrDefault("TopicArn") == b && survivor.GetValueOrDefault("DisplayName") == "away twin",
            "deleting the home twin disturbed the away twin");
        var (code2, message) = await Error(() => sns.GetTopicAttributesAsync(new GetTopicAttributesRequest { TopicArn = a }));
        Ensure(code2 == "NotFound" && message == "Topic does not exist",
            $"the deleted twin must read as missing; got '{code2}': '{message}'");
    }
}
