// Translated from behavioral/events/check_event_bus_tag_limit_counts_tags_already_on_the_bus_and_overwrites_do_not_count.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.EventBridge.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Events;

/// <summary>events.event-bus-tag-limit-counts-tags-already-on-the-bus-and-overwrites-do-not-count</summary>
[Category("AwsEvaluationSuite")]
public sealed class EventBusTagLimitCountsTagsAlreadyOnTheBusAndOverwritesDoNotCount
{
    private const int Limit = 50;
    private const string TooMany = "The request exceeds the maximum number of tags allowed per resource.";

    /// <summary>
    /// The 50-tag bus limit counts tags already on the bus: 40+11 is refused with nothing applied,
    /// 40+10 is accepted, a 51st new key is refused but an overwrite is not, and a 51-tag create fails.
    /// </summary>
    [Test]
    [DisplayName("events.event-bus-tag-limit-counts-tags-already-on-the-bus-and-overwrites-do-not-count")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    [KnownFidelityGap(FidelityGap.EventBridgeTags, "ListTagsForResource is not supported (NotSupportedException)")]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        // Config(retries={"total_max_attempts": 1}): the context's clients never retry.
        var events = ctx.Events;
        var bus = FidelityContext.UniqueName("sim2real-behav-bus-");
        var arn = (await events.CreateEventBusAsync(new CreateEventBusRequest
        {
            Name = bus,
            Tags = [.. Enumerable.Range(0, 40).Select(i => new Tag { Key = $"base-{i:00}", Value = "v" })],
        })).EventBusArn;
        ctx.OnCleanup(() => events.DeleteEventBusAsync(new DeleteEventBusRequest { Name = bus }));

        async Task<Dictionary<string, string>> Tags() =>
            ((await events.ListTagsForResourceAsync(new ListTagsForResourceRequest { ResourceARN = arn })).Tags ?? [])
                .ToDictionary(t => t.Key, t => t.Value, StringComparer.Ordinal);

        Ensure((await Tags()).Count == 40, $"the bus was created with 40 tags but lists {(await Tags()).Count}");

        var (code, msg) = await Error(() => events.TagResourceAsync(new TagResourceRequest
        {
            ResourceARN = arn,
            Tags = [.. Enumerable.Range(0, 11).Select(i => new Tag { Key = $"over-{i:00}", Value = "v" })],
        }));
        Ensure(
            (code, msg) == ("ValidationException", TooMany),
            $"adding 11 tags to a bus with 40 gave '{code}': '{msg}'");
        var after = await Tags();
        Ensure(
            after.Count == 40 && !after.Keys.Any(k => k.StartsWith("over-", StringComparison.Ordinal)),
            $"the refused TagResource changed the bus's tags: {after.Count} tags, " +
            $"{after.Keys.Count(k => k.StartsWith("over-", StringComparison.Ordinal))} of the refused keys present");

        await events.TagResourceAsync(new TagResourceRequest
        {
            ResourceARN = arn,
            Tags = [.. Enumerable.Range(0, 10).Select(i => new Tag { Key = $"fill-{i:00}", Value = "v" })],
        });
        Ensure((await Tags()).Count == Limit, $"after adding 10 tags the bus lists {(await Tags()).Count}");

        (code, msg) = await Error(() => events.TagResourceAsync(new TagResourceRequest { ResourceARN = arn, Tags = [new Tag { Key = "one-more", Value = "v" }] }));
        Ensure(
            (code, msg) == ("ValidationException", TooMany),
            $"a 51st tag on a full bus gave '{code}': '{msg}'");
        Ensure(!(await Tags()).ContainsKey("one-more"), "the refused 51st tag is on the bus");

        await events.TagResourceAsync(new TagResourceRequest { ResourceARN = arn, Tags = [new Tag { Key = "base-00", Value = "overwritten" }] });
        var final = await Tags();
        Ensure(
            (final.Count, final.GetValueOrDefault("base-00")) == (Limit, "overwritten"),
            $"overwriting a key on a full bus must succeed without adding a tag: {final.Count} tags, " +
            $"base-00='{final.GetValueOrDefault("base-00")}'");

        var other = FidelityContext.UniqueName("sim2real-behav-bus-");
        (code, msg) = await Error(() => events.CreateEventBusAsync(new CreateEventBusRequest
        {
            Name = other,
            Tags = [.. Enumerable.Range(0, Limit + 1).Select(i => new Tag { Key = $"k-{i:00}", Value = "v" })],
        }));
        if (code == "<created>")
        {
            ctx.OnCleanup(() => events.DeleteEventBusAsync(new DeleteEventBusRequest { Name = other }));
        }

        Ensure(
            (code, msg) == ("ValidationException", TooMany),
            $"CreateEventBus with 51 tags gave '{code}': '{msg}'");
        (code, msg) = await Error(() => events.DescribeEventBusAsync(new DescribeEventBusRequest { Name = other }));
        Ensure(
            (code, msg) == ("ResourceNotFoundException", $"Event bus {other} does not exist."),
            $"the refused CreateEventBus left a bus behind: DescribeEventBus gave '{code}': '{msg}'");
    }

    /// <summary>(code, message) of the refusal; an unexpected success returns a value no code matches.</summary>
    private static async Task<(string? Code, string? Message)> Error(Func<Task> fn)
    {
        var error = await FidelityContext.CaptureError(fn);
        return error is null
            ? ("<created>", "the call succeeded; real AWS refuses it")
            : (error.Code, error.Message ?? "");
    }
}
