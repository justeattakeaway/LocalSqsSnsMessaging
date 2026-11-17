// Translated from behavioral/sns/check_fifo_topic_rejects_email_protocol.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.SimpleNotificationService;
using Amazon.SimpleNotificationService.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sns;

/// <summary>sns.fifo-topic-rejects-email-protocol</summary>
[Category("AwsEvaluationSuite")]
public sealed class FifoTopicRejectsEmailProtocol
{
    private const string Check = "fifo-topic-rejects-email-protocol";

    /// <summary>
    /// A FIFO topic refuses an email subscription with InvalidParameter and lists none, while an
    /// sqs subscription to the same topic succeeds.
    /// </summary>
    [Test]
    [DisplayName("sns.fifo-topic-rejects-email-protocol")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sns = ctx.Sns;
        var name = FidelityContext.UniqueName("sim2real-behav-sns-fifo-email-") + ".fifo";
        var topicArn = await CreateTopic(sns, ctx, name,
            new Dictionary<string, string> { ["FifoTopic"] = "true", ["ContentBasedDeduplication"] = "true" },
            [new Tag { Key = "sim2real-check", Value = Check }]);

        var queueArn = FakeSqsQueueArn(topicArn, FidelityContext.UniqueName("sim2real-behav-sns-fifo-email-q-"));
        var sqsSubArn = (await sns.SubscribeAsync(new SubscribeRequest { TopicArn = topicArn, Protocol = "sqs", Endpoint = queueArn })).SubscriptionArn;
        if (!string.IsNullOrEmpty(sqsSubArn) && sqsSubArn != "pending confirmation")
        {
            ctx.OnCleanup(() => sns.UnsubscribeAsync(new UnsubscribeRequest { SubscriptionArn = sqsSubArn }));
        }

        Ensure(sqsSubArn.StartsWith(topicArn + ":", StringComparison.Ordinal),
            $"positive control failed: expected an immediate sqs SubscriptionArn on the FIFO topic, got '{sqsSubArn}'");

        const string emailEndpoint = "sim2real-behav@example.com";
        var (code, message) = await Error(() => sns.SubscribeAsync(new SubscribeRequest { TopicArn = topicArn, Protocol = "email", Endpoint = emailEndpoint }));
        Ensure(code == "InvalidParameter", $"expected InvalidParameter for email protocol on a FIFO topic, got {code}");
        const string expected = "Invalid parameter: Invalid protocol type: email";
        Ensure(message == expected, $"expected message '{expected}', got '{message}'");

        var subs = (await sns.ListSubscriptionsByTopicAsync(new ListSubscriptionsByTopicRequest { TopicArn = topicArn })).Subscriptions ?? [];
        Ensure(!subs.Any(s => s.Protocol == "email"),
            $"a rejected email subscribe must leave no trace in ListSubscriptionsByTopic, got {subs.Count} subscription(s)");
    }

    /// <summary>
    /// Call fn(); return (code, message) of the ClientError it raises. An unexpected success
    /// raises, so a target that accepts what AWS refuses never reads as a code mismatch.
    /// </summary>
    private static async Task<(string? Code, string? Message)> Error<T>(Func<Task<T>> fn)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        return error is null
            ? throw Fail($"expected the call to be refused, but it succeeded and returned {result}")
            : (error.Code, error.Message);
    }

    /// <summary>CreateTopic + register best-effort DeleteTopic cleanup; returns the TopicArn.</summary>
    private static async Task<string> CreateTopic(AmazonSimpleNotificationServiceClient client, FidelityContext ctx, string name,
        Dictionary<string, string>? attributes = null, List<Tag>? tags = null)
    {
        var request = new CreateTopicRequest { Name = name };
        if (attributes is not null)
        {
            request.Attributes = attributes;
        }

        if (tags is not null)
        {
            request.Tags = tags;
        }

        var arn = (await client.CreateTopicAsync(request)).TopicArn;
        ctx.OnCleanup(() => client.DeleteTopicAsync(new DeleteTopicRequest { TopicArn = arn }));
        return arn;
    }

    /// <summary>
    /// A well-formed SQS queue ARN in the topic's own account/region -- SNS confirms sqs
    /// subscriptions without verifying the queue exists, so this is enough for a positive control.
    /// </summary>
    private static string FakeSqsQueueArn(string topicArn, string name)
    {
        var parts = topicArn.Split(':');
        return $"arn:aws:sqs:{parts[3]}:{parts[4]}:{name}";
    }
}
