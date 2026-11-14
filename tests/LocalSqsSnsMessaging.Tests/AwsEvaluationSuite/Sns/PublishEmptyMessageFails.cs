// Translated from behavioral/sns/check_publish_empty_message_fails.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.SimpleNotificationService.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sns;

/// <summary>sns.publish-empty-message-fails</summary>
[Category("AwsEvaluationSuite")]
public sealed class PublishEmptyMessageFails
{
    /// <summary>
    /// Publish with an empty Message is refused with InvalidParameter, while a non-empty publish
    /// to the same topic succeeds (so the refusal is due to the empty Message).
    /// </summary>
    [Test]
    [DisplayName("sns.publish-empty-message-fails")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    [KnownFidelityGap(FidelityGap.SnsValidation, "Publish with an empty Message is accepted instead of refused with InvalidParameter 'Invalid parameter: Empty message'")]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sns = ctx.Sns;
        var arn = await CreateTopic(ctx);

        var messageId = (await sns.PublishAsync(new PublishRequest { TopicArn = arn, Message = "positive control" })).MessageId;
        Ensure(!string.IsNullOrEmpty(messageId), "the positive control publish did not return a MessageId");

        var error = await ClientError(() => sns.PublishAsync(new PublishRequest { TopicArn = arn, Message = "" }));
        Ensure(error.Code == "InvalidParameter", $"expected InvalidParameter for an empty Message, got '{error.Code}'");
        Ensure(error.Message == "Invalid parameter: Empty message",
            $"expected the exact message 'Invalid parameter: Empty message', got '{error.Message}'");
    }

    /// <summary>Return the Error dict of the ClientError fn() raises; an unexpected success raises.</summary>
    private static async Task<AwsError> ClientError<T>(Func<Task<T>> fn)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        return error ?? throw Fail($"real AWS rejects this call, but it SUCCEEDED and returned {result}");
    }

    /// <summary>Create a topic, register its teardown, and return its ARN.</summary>
    private static async Task<string> CreateTopic(FidelityContext ctx, string? name = null)
    {
        var sns = ctx.Sns;
        name ??= FidelityContext.UniqueName("sim2real-behav-sns-topic-");
        var arn = (await sns.CreateTopicAsync(new CreateTopicRequest
        {
            Name = name, Tags = [new() { Key = "sim2real", Value = "behavioral" }],
        })).TopicArn;
        ctx.OnCleanup(() => sns.DeleteTopicAsync(new DeleteTopicRequest { TopicArn = arn }));
        return arn;
    }
}
