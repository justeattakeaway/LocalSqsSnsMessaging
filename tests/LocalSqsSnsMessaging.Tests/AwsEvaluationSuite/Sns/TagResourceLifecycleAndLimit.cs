// Translated from behavioral/sns/check_tag_resource_lifecycle_and_limit.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.SimpleNotificationService.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sns;

/// <summary>sns.tag-resource-lifecycle-and-limit</summary>
[Category("AwsEvaluationSuite")]
public sealed class TagResourceLifecycleAndLimit
{
    /// <summary>TagResource/UntagResource mutate the tag set as expected, and exceeding 50 tags is rejected.</summary>
    [Test]
    [DisplayName("sns.tag-resource-lifecycle-and-limit")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sns = ctx.Sns;
        var name = FidelityContext.UniqueName("sim2real-behav-topic-tags-");
        var arn = (await sns.CreateTopicAsync(new CreateTopicRequest { Name = name })).TopicArn;
        ctx.OnCleanup(() => sns.DeleteTopicAsync(new DeleteTopicRequest { TopicArn = arn }));

        await sns.TagResourceAsync(new TagResourceRequest { ResourceArn = arn, Tags = [new() { Key = "env", Value = "test" }] });
        var tags = (await sns.ListTagsForResourceAsync(new ListTagsForResourceRequest { ResourceArn = arn })).Tags ?? [];
        Ensure(tags.Any(t => t.Key == "env" && t.Value == "test"), $"expected tag env=test present, got {Show(tags)}");

        await sns.UntagResourceAsync(new UntagResourceRequest { ResourceArn = arn, TagKeys = ["env"] });
        tags = (await sns.ListTagsForResourceAsync(new ListTagsForResourceRequest { ResourceArn = arn })).Tags ?? [];
        Ensure(tags.All(t => t.Key != "env"), $"expected tag 'env' removed, still present in {Show(tags)}");

        var tooMany = Enumerable.Range(0, 51).Select(i => new Amazon.SimpleNotificationService.Model.Tag { Key = $"k{i}", Value = "v" }).ToList();
        var (code, message) = await Error(() => sns.TagResourceAsync(new TagResourceRequest { ResourceArn = arn, Tags = tooMany }));
        Ensure(code == "TagLimitExceeded", $"expected TagLimitExceeded for >50 tags, got {code}");
        Ensure(message == "Could not complete request: tag quota of per resource exceeded", $"unexpected TagLimitExceeded message: '{message}'");

        var surviving = (await sns.ListTagsForResourceAsync(new ListTagsForResourceRequest { ResourceArn = arn })).Tags ?? [];
        Ensure(!surviving.Any(t => t.Key.StartsWith('k')),
            $"rejected tag_resource must not partially apply tags, found {Show(surviving)}");
    }

    private static string Show(List<Amazon.SimpleNotificationService.Model.Tag> tags) =>
        $"[{string.Join(", ", tags.Select(t => $"{{'Key': '{t.Key}', 'Value': '{t.Value}'}}"))}]";

    /// <summary>
    /// Call fn(); return botocore (Error.Code, Error.Message). An unexpected SUCCESS raises, because
    /// an emulator ACCEPTING input real AWS refuses is the strongest signal available.
    /// </summary>
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
}
