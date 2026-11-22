// Translated from behavioral/sns/check_untag_resource_nonexistent_topic_fails.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit abd0811).
using Amazon.SimpleNotificationService;
using Amazon.SimpleNotificationService.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.Sns;

/// <summary>sns.untag-resource-nonexistent-topic-fails</summary>
[Category("AwsEvaluationSuite")]
public sealed class UntagResourceNonexistentTopicFails
{
    /// <summary>
    /// UntagResource on a deleted topic fails with ResourceNotFound, after removing a tag from
    /// the same topic while it was live, and succeeds again once the topic is recreated.
    /// </summary>
    [Test]
    [DisplayName("sns.untag-resource-nonexistent-topic-fails")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sns = ctx.Sns;
        var name = FidelityContext.UniqueName("sim2real-behav-sns-untag-");
        const string key = "sim2real-behav-sns-untag-key";
        var arn = (await sns.CreateTopicAsync(name)).TopicArn;
        ctx.OnCleanup(() => sns.DeleteTopicAsync(arn));

        await sns.TagResourceAsync(new TagResourceRequest { ResourceArn = arn, Tags = [new Tag { Key = key, Value = "live" }] });

        // Positive control: UntagResource on a live topic actually removes the tag.
        var liveBefore = await TagMap(sns, arn);
        Ensure(liveBefore.GetValueOrDefault(key) == "live", $"positive control: expected tag {Py.Repr(key)}='live', got {Py.Repr(liveBefore)}");
        await sns.UntagResourceAsync(new UntagResourceRequest { ResourceArn = arn, TagKeys = [key] });
        var liveAfter = await TagMap(sns, arn);
        Ensure(!liveAfter.ContainsKey(key), $"positive control: expected {Py.Repr(key)} removed, still present in {Py.Repr(liveAfter)}");

        // Break exactly one thing: the topic no longer exists.
        await sns.DeleteTopicAsync(arn);
        var (goneCode, goneMessage) = await Error(() => sns.GetTopicAttributesAsync(arn));
        Ensure(goneCode == "NotFound", $"expected NotFound reading a deleted topic, got {Py.Repr(goneCode)}");
        Ensure(goneMessage == "Topic does not exist", $"unexpected NotFound message reading a deleted topic: {Py.Repr(goneMessage)}");

        var (code, message) = await Error(() => sns.UntagResourceAsync(new UntagResourceRequest { ResourceArn = arn, TagKeys = [key] }));
        Ensure(code == "ResourceNotFound", $"UntagResource on a deleted topic: expected ResourceNotFound, got {Py.Repr(code)}");
        Ensure(message == "Resource does not exist",
            $"unexpected ResourceNotFound message for UntagResource on a deleted topic: {Py.Repr(message)}");

        // Restore: SNS topic ARNs are deterministic on account+region+name, so recreating reuses the
        // ARN already registered for cleanup above.
        var newArn = (await sns.CreateTopicAsync(name)).TopicArn;
        Ensure(newArn == arn, $"expected the recreated topic to reuse the ARN, got {Py.Repr(newArn)} vs {Py.Repr(arn)}");
        await sns.TagResourceAsync(new TagResourceRequest { ResourceArn = newArn, Tags = [new Tag { Key = key, Value = "restored" }] });
        var restoredBefore = await TagMap(sns, newArn);
        Ensure(restoredBefore.GetValueOrDefault(key) == "restored",
            $"expected the recreated topic to carry tag {Py.Repr(key)}='restored', got {Py.Repr(restoredBefore)}");
        await sns.UntagResourceAsync(new UntagResourceRequest { ResourceArn = newArn, TagKeys = [key] });
        var restoredAfter = await TagMap(sns, newArn);
        Ensure(!restoredAfter.ContainsKey(key),
            $"expected UntagResource to succeed again on the recreated topic, still present in {Py.Repr(restoredAfter)}");
    }

    private static async Task<Dictionary<string, string>> TagMap(AmazonSimpleNotificationServiceClient sns, string arn) =>
        ((await sns.ListTagsForResourceAsync(new ListTagsForResourceRequest { ResourceArn = arn })).Tags ?? [])
            .ToDictionary(t => t.Key, t => t.Value);

    /// <summary>(code, message) of the ClientError fn raises; an unexpected success raises too.</summary>
    private static async Task<(string? Code, string? Message)> Error<T>(Func<Task<T>> fn)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        return error is null
            ? throw Fail($"expected the call to be refused, but it succeeded and returned {result}")
            : (error.Code, error.Message);
    }
}
