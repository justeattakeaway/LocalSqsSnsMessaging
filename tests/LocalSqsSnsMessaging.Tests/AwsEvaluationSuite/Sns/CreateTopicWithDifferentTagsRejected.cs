// Translated from behavioral/sns/check_create_topic_with_different_tags_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.SimpleNotificationService;
using Amazon.SimpleNotificationService.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sns;

/// <summary>sns.create-topic-with-different-tags-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class CreateTopicWithDifferentTagsRejected
{
    private const string Check = "create-topic-with-different-tags-rejected";

    /// <summary>
    /// Re-running CreateTopic on an existing name with different Tags is refused and leaves the
    /// tags untouched; identical Tags or no Tags is idempotent and returns the same TopicArn.
    /// </summary>
    [Test]
    [DisplayName("sns.create-topic-with-different-tags-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    [KnownFidelityGap(FidelityGap.SnsTags, "CreateTopic on an existing name with different Tags succeeds instead of InvalidParameter 'Topic already exists with different tags'")]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sns = ctx.Sns;
        var name = FidelityContext.UniqueName("sim2real-behav-sns-tagconf-");
        List<Tag> original = [new() { Key = "sim2real", Value = Check }, new() { Key = "team", Value = "alpha" }];
        var arn = (await sns.CreateTopicAsync(new CreateTopicRequest { Name = name, Tags = original })).TopicArn;
        ctx.OnCleanup(() => sns.DeleteTopicAsync(new DeleteTopicRequest { TopicArn = arn }));

        var same = (await sns.CreateTopicAsync(new CreateTopicRequest { Name = name, Tags = original })).TopicArn;
        Ensure(same == arn, "CreateTopic with identical Tags must return the existing TopicArn");
        var bare = (await sns.CreateTopicAsync(new CreateTopicRequest { Name = name })).TopicArn;
        Ensure(bare == arn, "CreateTopic with no Tags on an existing name must return the existing TopicArn");

        List<Tag> changed = [new() { Key = "sim2real", Value = Check }, new() { Key = "team", Value = "beta" }];
        var (code, message) = await Error(() => sns.CreateTopicAsync(new CreateTopicRequest { Name = name, Tags = changed }));
        Ensure(code == "InvalidParameter", $"CreateTopic with different Tags gave '{code}'");
        Ensure(message == "Invalid parameter: Tags Reason: Topic already exists with different tags",
            $"unexpected message for CreateTopic with different Tags: '{message}'");

        var stored = await Tags(sns, arn);
        var want = new Dictionary<string, string> { ["sim2real"] = Check, ["team"] = "alpha" };
        Ensure(stored.Count == want.Count && !stored.Except(want).Any(),
            $"the refused CreateTopic must not change the topic's tags, got {{{string.Join(", ", stored.Select(kv => $"'{kv.Key}': '{kv.Value}'"))}}}");
    }

    private static async Task<Dictionary<string, string>> Tags(AmazonSimpleNotificationServiceClient sns, string arn) =>
        ((await sns.ListTagsForResourceAsync(new ListTagsForResourceRequest { ResourceArn = arn })).Tags ?? [])
        .ToDictionary(t => t.Key, t => t.Value);

    /// <summary>Return (Error.Code, Error.Message) of the ClientError fn raises; a success raises.</summary>
    private static async Task<(string? Code, string? Message)> Error<T>(Func<Task<T>> fn)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is null)
        {
            throw Fail($"expected the call to be rejected, but it SUCCEEDED and returned {result}");
        }

        return (error.Code, error.Message);
    }
}
