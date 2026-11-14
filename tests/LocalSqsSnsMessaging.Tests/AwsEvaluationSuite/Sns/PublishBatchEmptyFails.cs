// Translated from behavioral/sns/check_publish_batch_empty_fails.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.SimpleNotificationService.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sns;

/// <summary>sns.publish-batch-empty-fails</summary>
[Category("AwsEvaluationSuite")]
public sealed class PublishBatchEmptyFails
{
    private const string Check = "publish-batch-empty-fails";

    /// <summary>
    /// Call fn(); return botocore (Error.Code, Error.Message) if a ClientError was raised. An unexpected
    /// SUCCESS raises, because an emulator ACCEPTING input real AWS refuses is the strongest signal available.
    /// </summary>
    private static async Task<(string? Code, string? Message)> Error(Func<Task<PublishBatchResponse>> fn)
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is null)
        {
            throw Fail($"expected a rejection, but the call SUCCEEDED and returned {Describe(result!)}");
        }

        return (error.Code, error.Message);
    }

    private static string Describe(PublishBatchResponse r) =>
        $"{{'Successful': [{string.Join(", ", (r.Successful ?? []).Select(e => $"{{'Id': '{e.Id}', 'MessageId': '{e.MessageId}'}}"))}], " +
        $"'Failed': [{string.Join(", ", (r.Failed ?? []).Select(e => $"{{'Id': '{e.Id}', 'Code': '{e.Code}', 'Message': '{e.Message}'}}"))}]}}";

    private static List<Tag> TagsKv() =>
        [new() { Key = "sim2real", Value = Check }, new() { Key = "sim2real:managed", Value = "true" }];

    /// <summary>Create a topic, register its teardown, and return its ARN.</summary>
    private static async Task<string> CreateTopic(FidelityContext ctx, string? name = null)
    {
        var sns = ctx.Sns;
        name ??= FidelityContext.UniqueName("sim2real-behav-sns-topic-");
        var arn = (await sns.CreateTopicAsync(new CreateTopicRequest { Name = name, Tags = TagsKv() })).TopicArn;
        ctx.OnCleanup(() => sns.DeleteTopicAsync(new DeleteTopicRequest { TopicArn = arn }));
        return arn;
    }

    /// <summary>
    /// [overly-permissive] PublishBatch with an empty entry list must be rejected, and the same
    /// topic must still accept a normal batch -- otherwise "rejects empty" is indistinguishable from
    /// "rejects every PublishBatch".
    /// </summary>
    [Test]
    [DisplayName("sns.publish-batch-empty-fails")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    [KnownFidelityGap(FidelityGap.SnsBatch, "PublishBatch with no entries fails with HTTP 500 (an ArgumentNullException JSON body) instead of EmptyBatchRequest")]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sns = ctx.Sns;
        var arn = await CreateTopic(ctx);
        var (code, message) = await Error(
            () => sns.PublishBatchAsync(new PublishBatchRequest { TopicArn = arn, PublishBatchRequestEntries = [] }));
        Ensure(code == "EmptyBatchRequest", $"got '{code}'");
        Ensure(message == "The batch request doesn't contain any entries.", $"got '{message}'");

        // Positive control: the same topic and operation accept a real entry.
        var resp = await sns.PublishBatchAsync(new PublishBatchRequest
        {
            TopicArn = arn,
            PublishBatchRequestEntries = [new() { Id = "e0", Message = "positive-control" }],
        });
        Ensure((resp.Successful ?? []).Count == 1 && (resp.Failed ?? []).Count == 0,
            $"expected the positive-control entry to succeed, got {Describe(resp)}");
    }
}
