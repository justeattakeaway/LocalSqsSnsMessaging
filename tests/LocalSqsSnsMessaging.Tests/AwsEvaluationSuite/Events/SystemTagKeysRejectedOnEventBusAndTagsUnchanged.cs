// Translated from behavioral/events/check_system_tag_keys_rejected_on_event_bus_and_tags_unchanged.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.EventBridge;
using Amazon.EventBridge.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Events;

/// <summary>events.system-tag-keys-rejected-on-event-bus-and-tags-unchanged</summary>
[Category("AwsEvaluationSuite")]
public sealed class SystemTagKeysRejectedOnEventBusAndTagsUnchanged
{
    private const string Check = "system-tag-keys-rejected-on-event-bus-and-tags-unchanged";

    /// <summary>
    /// TagResource and UntagResource on a custom bus refuse reserved "aws:" tag keys with
    /// ValidationException, leaving the tags unchanged; a TagResource mixing a legal and a reserved key
    /// is refused as a whole, so the legal key is not written either.
    /// </summary>
    [Test]
    [DisplayName("events.system-tag-keys-rejected-on-event-bus-and-tags-unchanged")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var events = ctx.Events;
        var bus = FidelityContext.UniqueName("sim2real-behav-bus-");
        var arn = (await events.CreateEventBusAsync(new CreateEventBusRequest
        {
            Name = bus,
            Tags = [new Tag { Key = "sim2real", Value = Check }],
        })).EventBusArn;
        ctx.OnCleanup(() => events.DeleteEventBusAsync(new DeleteEventBusRequest { Name = bus }));

        await events.TagResourceAsync(new TagResourceRequest { ResourceARN = arn, Tags = [new Tag { Key = "team", Value = "sim2real" }] });
        var baseline = await Tags(events, arn);
        Ensure(
            DictEquals(baseline, new() { ["sim2real"] = Check, ["team"] = "sim2real" }),
            $"positive control: tags are {Show(baseline)}");

        const string expected = "Add/Remove system tags is not allowed.";

        var (code, message) = await Error(() => events.TagResourceAsync(new TagResourceRequest
        {
            ResourceARN = arn,
            Tags = [new Tag { Key = "aws:sim2real", Value = "x" }],
        }));
        Ensure((code, message) == ("ValidationException", expected), $"TagResource aws: key gave '{code}' '{message}'");
        Ensure(DictEquals(await Tags(events, arn), baseline), "the refused TagResource changed the bus tags");

        (code, message) = await Error(() => events.TagResourceAsync(new TagResourceRequest
        {
            ResourceARN = arn,
            Tags = [new Tag { Key = "legal", Value = "y" }, new Tag { Key = "aws:sim2real", Value = "x" }],
        }));
        Ensure(
            (code, message) == ("ValidationException", expected),
            $"TagResource mixing a legal and an aws: key gave '{code}' '{message}'");
        var afterMixed = await Tags(events, arn);
        Ensure(
            DictEquals(afterMixed, baseline),
            $"a refused TagResource is atomic: the legal key must not be written either, tags are {Show(afterMixed)}");

        (code, message) = await Error(() => events.UntagResourceAsync(new UntagResourceRequest { ResourceARN = arn, TagKeys = ["aws:sim2real"] }));
        Ensure((code, message) == ("ValidationException", expected), $"UntagResource aws: key gave '{code}' '{message}'");
        Ensure(DictEquals(await Tags(events, arn), baseline), "the refused UntagResource changed the bus tags");
    }

    // Python indexes ["Tags"], which raises KeyError when the response omits it.
    private static async Task<Dictionary<string, string>> Tags(AmazonEventBridgeClient events, string arn) =>
        ((await events.ListTagsForResourceAsync(new ListTagsForResourceRequest { ResourceARN = arn })).Tags
            ?? throw new KeyNotFoundException("'Tags'"))
        .ToDictionary(t => t.Key, t => t.Value);

    /// <summary>(code, message) of a call that must fail; an unexpected success raises, naming the result.</summary>
    private static async Task<(string? Code, string? Message)> Error<T>(Func<Task<T>> fn)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is not null)
        {
            return (error.Code, error.Message);
        }

        throw Fail($"expected the call to be refused, but it succeeded: {result}");
    }

    private static bool DictEquals(Dictionary<string, string> a, Dictionary<string, string> b)
        => a.Count == b.Count && a.All(kv => b.TryGetValue(kv.Key, out var v) && v == kv.Value);

    private static string Show(Dictionary<string, string> d) => "{" + string.Join(", ", d.Select(kv => $"'{kv.Key}': '{kv.Value}'")) + "}";
}
