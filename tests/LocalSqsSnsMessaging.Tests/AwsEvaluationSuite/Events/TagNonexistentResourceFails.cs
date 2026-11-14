// Translated from behavioral/events/check_tag_nonexistent_resource_fails.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.EventBridge;
using Amazon.EventBridge.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Events;

/// <summary>events.tag-nonexistent-resource-fails</summary>
[Category("AwsEvaluationSuite")]
public sealed class TagNonexistentResourceFails
{
    /// <summary>
    /// TagResource on the ARN of a deleted event bus fails with ResourceNotFoundException; the same
    /// ARN is first tagged successfully while the bus exists, as a positive control.
    /// </summary>
    [Test]
    [DisplayName("events.tag-nonexistent-resource-fails")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ev = ctx.Events;
        var bus = FidelityContext.UniqueName("sim2real-behav-bus-");
        var resp = await ev.CreateEventBusAsync(new CreateEventBusRequest { Name = bus });
        var arn = resp.EventBusArn;
        ctx.OnCleanup(() => DeleteBusIfPresent(ev, bus));
        await ev.TagResourceAsync(new TagResourceRequest { ResourceARN = arn, Tags = [new Tag { Key = "k", Value = "v" }] });
        var listed = await ev.ListTagsForResourceAsync(new ListTagsForResourceRequest { ResourceARN = arn });
        var liveTags = (listed.Tags ?? []).ToDictionary(t => t.Key, t => t.Value);
        Ensure(liveTags.GetValueOrDefault("k") == "v", $"expected positive control tag visible, got {Show(liveTags)}");
        await ev.DeleteEventBusAsync(new DeleteEventBusRequest { Name = bus });
        var (result, err) = await FidelityContext.Capture(() =>
            ev.TagResourceAsync(new TagResourceRequest { ResourceARN = arn, Tags = [new Tag { Key = "k2", Value = "v2" }] }));
        if (err is null)
        {
            throw Fail($"TagResource against a deleted bus SUCCEEDED: {result}");
        }

        Ensure(err.Code == "ResourceNotFoundException", $"expected ResourceNotFoundException, got {err}");
        var expectedMessage = $"Event bus {bus} does not exist.";
        Ensure(err.Message == expectedMessage, $"expected message '{expectedMessage}', got '{err.Message}'");
    }

    /// <summary>Teardown: the check deletes the bus itself, so a second delete may find nothing.</summary>
    private static async Task DeleteBusIfPresent(AmazonEventBridgeClient ev, string name)
    {
        var error = await FidelityContext.CaptureError(() => ev.DeleteEventBusAsync(new DeleteEventBusRequest { Name = name }));
        if (error is not null && error.Code != "ResourceNotFoundException")
        {
            throw error.Exception;
        }
    }

    private static string Show(Dictionary<string, string> d) => "{" + string.Join(", ", d.Select(kv => $"'{kv.Key}': '{kv.Value}'")) + "}";
}
