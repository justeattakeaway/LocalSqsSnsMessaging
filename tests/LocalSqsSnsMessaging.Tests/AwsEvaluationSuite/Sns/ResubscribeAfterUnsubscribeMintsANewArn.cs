// Translated from behavioral/sns/check_resubscribe_after_unsubscribe_mints_a_new_arn.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.SimpleNotificationService.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sns;

/// <summary>sns.resubscribe-after-unsubscribe-mints-a-new-arn</summary>
[Category("AwsEvaluationSuite")]
public sealed class ResubscribeAfterUnsubscribeMintsANewArn
{
    private const string Check = "resubscribe-after-unsubscribe-mints-a-new-arn";

    /// <summary>
    /// Re-subscribing a live endpoint returns the same ARN, but subscribing it after Unsubscribe
    /// mints a new ARN with default attributes while the old ARN stays NotFound.
    /// </summary>
    [Test]
    [DisplayName("sns.resubscribe-after-unsubscribe-mints-a-new-arn")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    [KnownFidelityGap(FidelityGap.SnsSubscriptions, "Subscribe to an sqs endpoint whose queue does not exist is refused with NotFound 'Queue not found.'; AWS accepts it without checking the queue")]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sns = ctx.Sns;
        var topicArn = (await sns.CreateTopicAsync(new CreateTopicRequest
        {
            Name = FidelityContext.UniqueName("sim2real-behav-sns-resub-"),
            Tags = [new() { Key = "sim2real", Value = Check }],
        })).TopicArn;
        ctx.OnCleanup(() => sns.DeleteTopicAsync(new DeleteTopicRequest { TopicArn = topicArn }));
        var parts = topicArn.Split(':');
        var (region, account) = (parts[3], parts[4]);
        var endpoint = $"arn:aws:sqs:{region}:{account}:{FidelityContext.UniqueName("sim2real-behav-sns-resub-q-")}";

        var first = (await sns.SubscribeAsync(new SubscribeRequest
        {
            TopicArn = topicArn, Protocol = "sqs", Endpoint = endpoint,
            Attributes = new() { ["RawMessageDelivery"] = "true" },
            ReturnSubscriptionArn = true,
        })).SubscriptionArn;
        ctx.OnCleanup(() => sns.UnsubscribeAsync(new UnsubscribeRequest { SubscriptionArn = first }));
        var again = (await sns.SubscribeAsync(new SubscribeRequest
        {
            TopicArn = topicArn, Protocol = "sqs", Endpoint = endpoint,
            Attributes = new() { ["RawMessageDelivery"] = "true" },
            ReturnSubscriptionArn = true,
        })).SubscriptionArn;
        Ensure(again == first, "re-subscribing a live endpoint must return the existing ARN");

        await sns.UnsubscribeAsync(new UnsubscribeRequest { SubscriptionArn = first });
        var second = (await sns.SubscribeAsync(new SubscribeRequest
        {
            TopicArn = topicArn, Protocol = "sqs", Endpoint = endpoint, ReturnSubscriptionArn = true,
        })).SubscriptionArn;
        ctx.OnCleanup(() => sns.UnsubscribeAsync(new UnsubscribeRequest { SubscriptionArn = second }));
        Ensure(second != first, "a subscription created after Unsubscribe must get a new ARN");
        Ensure(second.StartsWith(topicArn + ":", StringComparison.Ordinal), "the new subscription's ARN must sit under its topic");
        var attrs = (await sns.GetSubscriptionAttributesAsync(new GetSubscriptionAttributesRequest { SubscriptionArn = second })).Attributes ?? [];
        Ensure(attrs.GetValueOrDefault("RawMessageDelivery") == "false",
            $"the new subscription must start from defaults, RawMessageDelivery='{attrs.GetValueOrDefault("RawMessageDelivery")}'");
        var (code, message) = await Error(() => sns.GetSubscriptionAttributesAsync(new GetSubscriptionAttributesRequest { SubscriptionArn = first }));
        Ensure(code == "NotFound", $"the old subscription ARN must stay gone, got '{code}'");
        Ensure(message == "Subscription does not exist", $"unexpected NotFound message: '{message}'");
    }

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
