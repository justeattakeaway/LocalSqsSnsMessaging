// Translated from behavioral/sns/check_unsubscribe_pending_subscription_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.SimpleNotificationService.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.Sns;

/// <summary>sns.unsubscribe-pending-subscription-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class UnsubscribePendingSubscriptionRejected
{
    private const string Check = "unsubscribe-pending-subscription-rejected";

    /// <summary>
    /// Unsubscribe on a pending email subscription's real ARN is refused and it stays pending, while
    /// Unsubscribe on a confirmed sqs subscription of the same topic succeeds and removes it.
    /// </summary>
    [Test]
    [DisplayName("sns.unsubscribe-pending-subscription-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sns = ctx.Sns;
        var topicArn = (await sns.CreateTopicAsync(new CreateTopicRequest
        {
            Name = FidelityContext.UniqueName("sim2real-behav-sns-unsubpend-"),
            Tags = [new Tag { Key = "sim2real", Value = Check }],
        })).TopicArn;
        // A pending subscription cannot be unsubscribed; DeleteTopic removes it with the topic.
        ctx.OnCleanup(() => sns.DeleteTopicAsync(new DeleteTopicRequest { TopicArn = topicArn }));
        var parts = topicArn.Split(':');
        var (region, account) = (parts[3], parts[4]);

        var pending = (await sns.SubscribeAsync(new SubscribeRequest
        {
            TopicArn = topicArn, Protocol = "email", ReturnSubscriptionArn = true,
            Endpoint = $"{FidelityContext.UniqueName("sim2real-behav-sns")}@example.com",
        })).SubscriptionArn;
        var confirmed = (await sns.SubscribeAsync(new SubscribeRequest
        {
            TopicArn = topicArn, Protocol = "sqs", ReturnSubscriptionArn = true,
            Endpoint = $"arn:aws:sqs:{region}:{account}:{FidelityContext.UniqueName("sim2real-behav-sns-unsubpend-q-")}",
        })).SubscriptionArn;
        ctx.OnCleanup(() => sns.UnsubscribeAsync(new UnsubscribeRequest { SubscriptionArn = confirmed }));

        var (code, message) = await Error(() => sns.UnsubscribeAsync(new UnsubscribeRequest { SubscriptionArn = pending }));
        Ensure(code == "InvalidParameter", $"Unsubscribe on a pending subscription gave '{code}'");
        Ensure(message == "Invalid parameter: SubscriptionArn Reason: Cannot unsubscribe a " +
                          "subscription that is pending confirmation",
            $"unexpected message for Unsubscribe on a pending subscription: '{message}'");
        var state = (await sns.GetSubscriptionAttributesAsync(new GetSubscriptionAttributesRequest { SubscriptionArn = pending })).Attributes ?? [];
        Ensure(state.GetValueOrDefault("PendingConfirmation") == "true",
            $"the refused Unsubscribe must leave the subscription pending, got '{state.GetValueOrDefault("PendingConfirmation")}'");

        await sns.UnsubscribeAsync(new UnsubscribeRequest { SubscriptionArn = confirmed });
        (code, message) = await Error(() => sns.GetSubscriptionAttributesAsync(new GetSubscriptionAttributesRequest { SubscriptionArn = confirmed }));
        Ensure(code == "NotFound", $"the unsubscribed confirmed subscription must be gone, got '{code}'");
        Ensure(message == "Subscription does not exist", $"unexpected NotFound message: '{message}'");
    }

    /// <summary>Return (Error.Code, Error.Message) of the ClientError fn raises; a success raises.</summary>
    private static async Task<(string? Code, string? Message)> Error<T>(Func<Task<T>> fn)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        return error is null
            ? throw Fail($"expected the call to be rejected, but it SUCCEEDED and returned {result}")
            : (error.Code, error.Message);
    }
}
