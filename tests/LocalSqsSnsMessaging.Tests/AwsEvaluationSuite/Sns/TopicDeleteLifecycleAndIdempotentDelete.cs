// Translated from behavioral/sns/check_topic_delete_lifecycle_and_idempotent_delete.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Net;
using Amazon.SimpleNotificationService;
using Amazon.SimpleNotificationService.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sns;

/// <summary>sns.topic-delete-lifecycle-and-idempotent-delete</summary>
[Category("AwsEvaluationSuite")]
public sealed class TopicDeleteLifecycleAndIdempotentDelete
{
    /// <summary>
    /// Deleting a topic removes it (GetTopicAttributes -> NotFound); redeleting is a no-op that
    /// leaves the topic exactly as gone.
    /// </summary>
    [Test]
    [DisplayName("sns.topic-delete-lifecycle-and-idempotent-delete")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ev = ctx.Sns;
        var name = FidelityContext.UniqueName("sim2real-behav-sns-del-");
        var arn = await CreateTopic(ev, ctx, name);
        await ev.GetTopicAttributesAsync(new GetTopicAttributesRequest { TopicArn = arn });
        await ev.DeleteTopicAsync(new DeleteTopicRequest { TopicArn = arn });
        var (code, message) = await GetTopicAttributesError(ev, arn);
        Ensure(code == "NotFound", $"expected NotFound after delete, got {code}");
        Ensure(message == "Topic does not exist", $"unexpected NotFound message after delete: '{message}'");
        var redelete = await ev.DeleteTopicAsync(new DeleteTopicRequest { TopicArn = arn });
        var redeleteStatus = redelete.HttpStatusCode;
        Ensure(redeleteStatus == HttpStatusCode.OK,
            $"expected redeleting an already-deleted topic to be a no-op (200), got {(int)redeleteStatus}");
        var (codeAfterRedelete, messageAfterRedelete) = await GetTopicAttributesError(ev, arn);
        Ensure(codeAfterRedelete == "NotFound",
            $"expected topic to remain gone (NotFound) after idempotent redelete, got {codeAfterRedelete}");
        Ensure(messageAfterRedelete == "Topic does not exist",
            $"unexpected NotFound message after idempotent redelete: '{messageAfterRedelete}'");
    }

    /// <summary>CreateTopic + register best-effort DeleteTopic cleanup; returns the TopicArn.</summary>
    private static async Task<string> CreateTopic(AmazonSimpleNotificationServiceClient client, FidelityContext ctx, string name)
    {
        var arn = (await client.CreateTopicAsync(new CreateTopicRequest { Name = name })).TopicArn;
        ctx.OnCleanup(() => client.DeleteTopicAsync(new DeleteTopicRequest { TopicArn = arn }));
        return arn;
    }

    /// <summary>(Code, Message) of the refused GetTopicAttributes. An unexpected SUCCESS raises.</summary>
    private static async Task<(string? Code, string? Message)> GetTopicAttributesError(AmazonSimpleNotificationServiceClient client, string arn)
    {
        var (result, error) = await FidelityContext.Capture(() => client.GetTopicAttributesAsync(new GetTopicAttributesRequest { TopicArn = arn }));
        if (error is null)
        {
            throw Fail($"GetTopicAttributes on deleted topic {arn} SUCCEEDED: {result}");
        }

        return (error.Code, error.Message);
    }
}
