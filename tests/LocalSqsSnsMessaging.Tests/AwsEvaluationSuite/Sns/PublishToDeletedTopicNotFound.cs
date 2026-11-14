// Translated from behavioral/sns/check_publish_to_deleted_topic_not_found.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.SimpleNotificationService;
using Amazon.SimpleNotificationService.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sns;

/// <summary>sns.publish-to-deleted-topic-not-found</summary>
[Category("AwsEvaluationSuite")]
public sealed class PublishToDeletedTopicNotFound
{
    /// <summary>
    /// Publish to a deleted (nonexistent) TopicArn raises NotFound; the deleted topic
    /// never reappears in ListTopics, and a live control topic still accepts Publish.
    /// </summary>
    [Test]
    [DisplayName("sns.publish-to-deleted-topic-not-found")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    [KnownFidelityGap(FidelityGap.SnsErrorMessages, "Publish to a deleted topic is refused with NotFound 'Topic not found: <arn>' instead of 'Topic does not exist'")]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ev = ctx.Sns;
        var name = FidelityContext.UniqueName("sim2real-behav-sns-pubgone-");
        var arn = await CreateTopic(ev, ctx, name);
        await ev.DeleteTopicAsync(new DeleteTopicRequest { TopicArn = arn });
        await WaitForTopicGone(ev, arn);

        var (code, message) = await Error(() => ev.PublishAsync(new PublishRequest { TopicArn = arn, Message = "ghost" }));
        Ensure(code == "NotFound", $"expected NotFound publishing to deleted topic, got {code}");
        Ensure(message == "Topic does not exist", $"expected the exact message 'Topic does not exist', got '{message}'");

        var allArns = new HashSet<string>();
        await foreach (var page in ev.Paginators.ListTopics(new ListTopicsRequest()).Responses)
        {
            foreach (var t in page.Topics ?? [])
            {
                allArns.Add(t.TopicArn);
            }
        }

        Ensure(!allArns.Contains(arn), $"deleted topic {arn} still present in ListTopics");

        // Positive control: Publish still succeeds against a live topic, so the NotFound
        // above is specific to the deleted topic rather than Publish being broken outright.
        var liveArn = await CreateTopic(ev, ctx, FidelityContext.UniqueName("sim2real-behav-sns-pubgone-live-"));
        var messageId = (await ev.PublishAsync(new PublishRequest { TopicArn = liveArn, Message = "alive" })).MessageId;
        Ensure(!string.IsNullOrEmpty(messageId), "expected a MessageId from Publish against a live topic");
    }

    /// <summary>CreateTopic + register best-effort DeleteTopic cleanup; returns the TopicArn.</summary>
    private static async Task<string> CreateTopic(AmazonSimpleNotificationServiceClient client, FidelityContext ctx, string name)
    {
        var arn = (await client.CreateTopicAsync(new CreateTopicRequest { Name = name })).TopicArn;
        ctx.OnCleanup(() => client.DeleteTopicAsync(new DeleteTopicRequest { TopicArn = arn }));
        return arn;
    }

    /// <summary>Call fn(); return the error (Code, Message). An unexpected SUCCESS raises.</summary>
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

    /// <summary>True while GetTopicAttributes still answers for arn; False once it reports NotFound.</summary>
    private static async Task<bool> TopicExists(AmazonSimpleNotificationServiceClient client, string arn)
    {
        var error = await FidelityContext.CaptureError(() => client.GetTopicAttributesAsync(new GetTopicAttributesRequest { TopicArn = arn }));
        if (error is null)
        {
            return true;
        }

        if (error.Code == "NotFound")
        {
            return false;
        }

        throw error.Exception;
    }

    private static async Task WaitForTopicGone(AmazonSimpleNotificationServiceClient client, string arn, double timeoutS = 30.0, double intervalS = 1.0)
    {
        var deadline = FidelityTime.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(timeoutS))
        {
            if (!await TopicExists(client, arn))
            {
                return;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(intervalS));
        }

        throw Fail($"topic {arn} never became NotFound within {timeoutS}s");
    }
}
