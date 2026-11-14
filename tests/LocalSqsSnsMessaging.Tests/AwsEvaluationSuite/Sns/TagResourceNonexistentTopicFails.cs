// Translated from behavioral/sns/check_tag_resource_nonexistent_topic_fails.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.SimpleNotificationService.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sns;

/// <summary>sns.tag-resource-nonexistent-topic-fails</summary>
[Category("AwsEvaluationSuite")]
public sealed class TagResourceNonexistentTopicFails
{
    /// <summary>Call fn(); return botocore (Error.Code, Error.Message). An unexpected SUCCESS raises.</summary>
    private static async Task<(string? Code, string? Message)> Error<T>(Func<Task<T>> fn)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is null)
        {
            throw Fail($"expected a ClientError, but the call SUCCEEDED and returned {result}");
        }

        return (error.Code, error.Message);
    }

    private static string Show(Dictionary<string, string> d) => "{" + string.Join(", ", d.Select(kv => $"'{kv.Key}': '{kv.Value}'")) + "}";

    /// <summary>
    /// TagResource succeeds on a live topic, fails ResourceNotFound once the topic is
    /// deleted, and succeeds again once a topic with the identical name — and therefore the
    /// identical ARN — is recreated.
    /// </summary>
    [Test]
    [DisplayName("sns.tag-resource-nonexistent-topic-fails")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sns = ctx.Sns;
        var name = FidelityContext.UniqueName("sim2real-behav-sns-topic-");
        var arn = (await sns.CreateTopicAsync(new CreateTopicRequest { Name = name })).TopicArn;
        ctx.OnCleanup(() => sns.DeleteTopicAsync(new DeleteTopicRequest { TopicArn = arn }));

        // positive control: tagging a live topic works and is visible via an independent read
        await sns.TagResourceAsync(new TagResourceRequest { ResourceArn = arn, Tags = [new() { Key = "k", Value = "v" }] });
        var liveTags = ((await sns.ListTagsForResourceAsync(new ListTagsForResourceRequest { ResourceArn = arn })).Tags ?? []).ToDictionary(t => t.Key, t => t.Value);
        Ensure(liveTags.GetValueOrDefault("k") == "v", $"positive control failed to tag live topic, got {Show(liveTags)}");

        // break exactly one thing: the topic no longer exists
        await sns.DeleteTopicAsync(new DeleteTopicRequest { TopicArn = arn });

        var (code, message) = await Error(
            () => sns.TagResourceAsync(new TagResourceRequest { ResourceArn = arn, Tags = [new() { Key = "k2", Value = "v2" }] }));
        Ensure(code == "ResourceNotFound", $"expected ResourceNotFound, got '{code}'");
        Ensure(message == "Resource does not exist", $"unexpected ResourceNotFound message for TagResource: '{message}'");

        // effect absent via an independent read at the same ARN
        var (listCode, listMessage) = await Error(() => sns.ListTagsForResourceAsync(new ListTagsForResourceRequest { ResourceArn = arn }));
        Ensure(listCode == "ResourceNotFound",
            $"expected ListTagsForResource to also see the topic gone, got '{listCode}'");
        Ensure(listMessage == "Resource does not exist",
            $"unexpected ResourceNotFound message for ListTagsForResource: '{listMessage}'");

        // restore: recreating the topic under the same name reuses the same ARN
        var restoredArn = (await sns.CreateTopicAsync(new CreateTopicRequest { Name = name })).TopicArn;
        Ensure(restoredArn == arn, $"expected recreated topic to reuse ARN {arn}, got {restoredArn}");

        // the identical call now succeeds
        await sns.TagResourceAsync(new TagResourceRequest { ResourceArn = arn, Tags = [new() { Key = "k2", Value = "v2" }] });
        var restoredTags = ((await sns.ListTagsForResourceAsync(new ListTagsForResourceRequest { ResourceArn = arn })).Tags ?? []).ToDictionary(t => t.Key, t => t.Value);
        Ensure(restoredTags.GetValueOrDefault("k2") == "v2", $"expected restored tagging to succeed, got {Show(restoredTags)}");
    }
}
