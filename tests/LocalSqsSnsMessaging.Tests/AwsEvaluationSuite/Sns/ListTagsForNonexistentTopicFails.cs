// Translated from behavioral/sns/check_list_tags_for_nonexistent_topic_fails.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.SimpleNotificationService.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sns;

/// <summary>sns.list-tags-for-nonexistent-topic-fails</summary>
[Category("AwsEvaluationSuite")]
public sealed class ListTagsForNonexistentTopicFails
{
    /// <summary>
    /// ListTagsForResource on a deleted topic fails with ResourceNotFound, after succeeding on
    /// the same topic while it was live (so a blanket refusal cannot pass).
    /// </summary>
    [Test]
    [DisplayName("sns.list-tags-for-nonexistent-topic-fails")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sns = ctx.Sns;
        var arn = await DeletedTopicArn(ctx);
        var (code, message) = await Error(() => sns.ListTagsForResourceAsync(new ListTagsForResourceRequest { ResourceArn = arn }));
        Ensure(code == "ResourceNotFound", $"expected ResourceNotFound, got '{code}'");
        const string expected = "Resource does not exist";
        Ensure(message == expected, $"expected message '{expected}', got '{message}'");
    }

    /// <summary>Return (code, message) of the ClientError fn() raises; an unexpected success raises.</summary>
    private static async Task<(string? Code, string? Message)> Error<T>(Func<Task<T>> fn)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is null)
        {
            throw Fail($"expected the call to be refused, but it succeeded and returned {result}");
        }

        return (error.Code, error.Message);
    }

    /// <summary>Create a topic, prove ListTagsForResource works on it live, delete it, and return its TopicArn.</summary>
    private static async Task<string> DeletedTopicArn(FidelityContext ctx)
    {
        var sns = ctx.Sns;
        var arn = (await sns.CreateTopicAsync(new CreateTopicRequest
        {
            Name = FidelityContext.UniqueName("sim2real-behav-sns-topic-"),
            Tags = [new() { Key = "sim2real", Value = "behav-sns-list-tags-nonexistent" }],
        })).TopicArn;
        // Registered anyway so a failure between create and delete cannot leak.
        ctx.OnCleanup(() => sns.DeleteTopicAsync(new DeleteTopicRequest { TopicArn = arn }));

        var liveTags = (await sns.ListTagsForResourceAsync(new ListTagsForResourceRequest { ResourceArn = arn })).Tags ?? [];
        Ensure(liveTags.ToDictionary(t => t.Key, t => t.Value).GetValueOrDefault("sim2real") == "behav-sns-list-tags-nonexistent",
            $"positive control failed: expected the sim2real tag on the live topic, got [{string.Join(", ", liveTags.Select(t => $"{{'Key': '{t.Key}', 'Value': '{t.Value}'}}"))}]");

        await sns.DeleteTopicAsync(new DeleteTopicRequest { TopicArn = arn });
        return arn;
    }
}
