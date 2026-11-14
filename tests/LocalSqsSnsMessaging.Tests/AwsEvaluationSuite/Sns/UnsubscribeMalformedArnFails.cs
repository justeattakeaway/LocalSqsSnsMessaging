// Translated from behavioral/sns/check_unsubscribe_malformed_arn_fails.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.SimpleNotificationService.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sns;

/// <summary>sns.unsubscribe-malformed-arn-fails</summary>
[Category("AwsEvaluationSuite")]
public sealed class UnsubscribeMalformedArnFails
{
    /// <summary>
    /// Unsubscribe with a malformed SubscriptionArn is rejected, the real subscription survives the
    /// rejected call, and unsubscribing it with its actual ARN still succeeds.
    /// </summary>
    [Test]
    [DisplayName("sns.unsubscribe-malformed-arn-fails")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    [KnownFidelityGap(FidelityGap.SnsSubscriptions, "Subscribing an SQS ARN with no queue behind it is refused NotFound 'Queue not found.' instead of being confirmed")]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sns = ctx.Sns;
        var topicArn = (await sns.CreateTopicAsync(new CreateTopicRequest { Name = FidelityContext.UniqueName("sim2real-behav-sns-unsubbad-") })).TopicArn;
        ctx.OnCleanup(() => sns.DeleteTopicAsync(new DeleteTopicRequest { TopicArn = topicArn }));

        var queueArn = FakeQueueArn(topicArn);
        var subArn = (await sns.SubscribeAsync(new SubscribeRequest { TopicArn = topicArn, Protocol = "sqs", Endpoint = queueArn })).SubscriptionArn;
        Ensure(!string.IsNullOrEmpty(subArn) && subArn != "pending confirmation",
            $"expected the sqs subscription to auto-confirm, got '{subArn}'");
        ctx.OnCleanup(() => sns.UnsubscribeAsync(new UnsubscribeRequest { SubscriptionArn = subArn }));

        var error = await ClientError(() => sns.UnsubscribeAsync(new UnsubscribeRequest { SubscriptionArn = "not-an-arn" }));
        Ensure(error.Code == "InvalidParameter", $"expected InvalidParameter for a malformed SubscriptionArn, got '{error.Code}'");
        Ensure(error.Message == "Invalid parameter: SubscriptionArn Reason: An ARN must have at least 6 elements, not 1",
            $"unexpected refusal message for a malformed SubscriptionArn: '{error.Message}'");

        var attrs = (await sns.GetSubscriptionAttributesAsync(new GetSubscriptionAttributesRequest { SubscriptionArn = subArn })).Attributes ?? [];
        Ensure(attrs.GetValueOrDefault("SubscriptionArn") == subArn,
            "the real subscription must survive the rejected malformed-ARN call, " +
            $"got attributes {{{string.Join(", ", attrs.Select(kv => $"'{kv.Key}': '{kv.Value}'"))}}}");

        await sns.UnsubscribeAsync(new UnsubscribeRequest { SubscriptionArn = subArn });
        var gone = await ClientError(() => sns.GetSubscriptionAttributesAsync(new GetSubscriptionAttributesRequest { SubscriptionArn = subArn }));
        Ensure(gone.Code == "NotFound", $"expected NotFound reading the real subscription after unsubscribing it, got '{gone.Code}'");
        Ensure(gone.Message == "Subscription does not exist", $"unexpected NotFound message reading the unsubscribed subscription: '{gone.Message}'");
    }

    /// <summary>Call fn() and return its botocore Error dict; an unexpected success raises.</summary>
    private static async Task<AwsError> ClientError<T>(Func<Task<T>> fn)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        return error ?? throw Fail($"expected a ClientError, call succeeded with {result}");
    }

    /// <summary>
    /// A well-formed SQS queue ARN in the topic's account/region (SNS confirms sqs subs
    /// without verifying the queue exists).
    /// </summary>
    private static string FakeQueueArn(string topicArn)
    {
        var parts = topicArn.Split(':');
        var name = $"sim2real-behav-sns-q-{Guid.NewGuid().ToString("N")[..8]}";
        return $"arn:aws:sqs:{parts[3]}:{parts[4]}:{name}";
    }
}
