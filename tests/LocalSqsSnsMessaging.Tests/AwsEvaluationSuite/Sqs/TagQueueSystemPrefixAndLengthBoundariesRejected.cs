// Translated from behavioral/sqs/check_tag_queue_system_prefix_and_length_boundaries_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sqs;

/// <summary>sqs.tag-queue-system-prefix-and-length-boundaries-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class TagQueueSystemPrefixAndLengthBoundariesRejected
{
    private const string KeyMsg = "Tag keys must be between 1 and 128 characters in length.";
    private const string ValueMsg = "Tag values must be between 0 and 256 characters in length.";
    private const string SystemMsg = "Caller is an end user and not allowed to mutate system tags";

    private static Dictionary<string, string> Tags() => new() { ["sim2real"] = "tag-queue-system-prefix-and-length-boundaries-rejected" };

    /// <summary>
    /// A 128-character tag key, 256-character value and empty value are stored; an 'aws:' key,
    /// empty key, 257-character value, or 129-character UntagQueue key is refused with
    /// InvalidParameterValue and the exact message, leaving the tag set unchanged.
    /// </summary>
    [Test]
    [DisplayName("sqs.tag-queue-system-prefix-and-length-boundaries-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sqs = ctx.Sqs;
        var url = (await sqs.CreateQueueAsync(new CreateQueueRequest { QueueName = FidelityContext.UniqueName("sim2real-behav-tagrules-"), Tags = Tags() })).QueueUrl;
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(url));

        var boundary = new Dictionary<string, string> { [new string('k', 128)] = "v", ["long-value"] = new string('v', 256), ["empty-value"] = "" };
        await sqs.TagQueueAsync(new TagQueueRequest { QueueUrl = url, Tags = new(boundary) });
        var want = Tags();
        foreach (var (k, v) in boundary)
        {
            want[k] = v;
        }

        var got = (await sqs.ListQueueTagsAsync(new ListQueueTagsRequest { QueueUrl = url })).Tags ?? [];
        Ensure(DictEquals(got, want),
            $"boundary tags read back as [{string.Join(", ", got.Select(kv => (kv.Key.Length > 12 ? kv.Key[..12] : kv.Key, kv.Key.Length, kv.Value.Length)).Order().Select(t => $"('{t.Item1}', {t.Item2}, {t.Item3})"))}]");

        foreach (var (label, tagset, msg) in new (string, Dictionary<string, string>, string)[]
        {
            ("aws: key", new() { ["aws:owner"] = "me", ["companion"] = "x" }, SystemMsg),
            ("empty key", new() { [""] = "v", ["companion"] = "x" }, KeyMsg),
            ("257-char value", new() { ["companion"] = "x", ["big"] = new string('v', 257) }, ValueMsg),
        })
        {
            var err = await Error(() => sqs.TagQueueAsync(new TagQueueRequest { QueueUrl = url, Tags = tagset }));
            Ensure((err.Code, err.Message) == ("InvalidParameterValue", msg), $"TagQueue with a {label} answered {err}");
            var afterTag = (await sqs.ListQueueTagsAsync(new ListQueueTagsRequest { QueueUrl = url })).Tags ?? [];
            Ensure(DictEquals(afterTag, want),
                $"the refused TagQueue ({label}) changed the tag set: added " +
                $"[{string.Join(", ", afterTag.Keys.Except(want.Keys).Order(StringComparer.Ordinal))}]");
        }

        var untagErr = await Error(() => sqs.UntagQueueAsync(new UntagQueueRequest { QueueUrl = url, TagKeys = ["long-value", new string('k', 129)] }));
        Ensure((untagErr.Code, untagErr.Message) == ("InvalidParameterValue", KeyMsg), $"UntagQueue with a 129-character key answered {untagErr}");
        var after = (await sqs.ListQueueTagsAsync(new ListQueueTagsRequest { QueueUrl = url })).Tags ?? [];
        Ensure(DictEquals(after, want),
            $"the refused UntagQueue removed [{string.Join(", ", want.Keys.Except(after.Keys).Order(StringComparer.Ordinal))}]");

        await sqs.UntagQueueAsync(new UntagQueueRequest { QueueUrl = url, TagKeys = [new string('k', 128), "long-value"] });
        var final = (await sqs.ListQueueTagsAsync(new ListQueueTagsRequest { QueueUrl = url })).Tags ?? [];
        var wantFinal = Tags();
        wantFinal["empty-value"] = "";
        Ensure(DictEquals(final, wantFinal),
            $"UntagQueue of the 128-character key and one other left [{string.Join(", ", final.Keys.Order(StringComparer.Ordinal))}]");
    }

    private static bool DictEquals(Dictionary<string, string> a, Dictionary<string, string> b)
        => a.Count == b.Count && a.All(kv => b.TryGetValue(kv.Key, out var v) && v == kv.Value);

    /// <summary>The botocore Error dict <paramref name="fn"/> raises. An unexpected success raises, naming the result.</summary>
    private static async Task<AwsError> Error(Func<Task> fn)
        => await FidelityContext.CaptureError(fn) ?? throw Fail("expected the call to be refused, but it succeeded");
}
