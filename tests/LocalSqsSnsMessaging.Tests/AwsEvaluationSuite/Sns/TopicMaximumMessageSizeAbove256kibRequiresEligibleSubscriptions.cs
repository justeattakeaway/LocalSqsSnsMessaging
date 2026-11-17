// Translated from behavioral/sns/check_topic_maximum_message_size_above_256kib_requires_eligible_subscriptions.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.SimpleNotificationService.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sns;

/// <summary>sns.topic-maximum-message-size-above-256kib-requires-eligible-subscriptions</summary>
[Category("AwsEvaluationSuite")]
public sealed class TopicMaximumMessageSizeAbove256kibRequiresEligibleSubscriptions
{
    private const string Check = "topic-maximum-message-size-above-256kib-requires-eligible-subscriptions";
    private const int Over256Kib = 300000;

    /// <summary>
    /// MaximumMessageSize above 256 KiB is refused InvalidParameter (left unset) while the topic
    /// has a non-SQS/Firehose/Lambda subscription, even a pending one; a topic whose only
    /// subscription is SQS accepts the same value.
    /// </summary>
    [Test]
    [DisplayName("sns.topic-maximum-message-size-above-256kib-requires-eligible-subscriptions")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sns = ctx.Sns;

        var ineligibleArn = (await sns.CreateTopicAsync(new CreateTopicRequest
        {
            Name = FidelityContext.UniqueName("sim2real-behav-sns-maxmsg-inel-"),
            Tags = [new Tag { Key = "sim2real", Value = Check }],
        })).TopicArn;
        ctx.OnCleanup(() => sns.DeleteTopicAsync(new DeleteTopicRequest { TopicArn = ineligibleArn }));
        var email = $"{FidelityContext.UniqueName("sim2real-behav-sns-maxmsg")}@example.com";
        // Email subscriptions stay PendingConfirmation forever without a real mailbox; DeleteTopic
        // (registered above) removes it, since a pending subscription cannot be Unsubscribed.
        await sns.SubscribeAsync(new SubscribeRequest { TopicArn = ineligibleArn, Protocol = "email", Endpoint = email });

        var (code, message) = await Error(() => sns.SetTopicAttributesAsync(new SetTopicAttributesRequest
        {
            TopicArn = ineligibleArn, AttributeName = "MaximumMessageSize", AttributeValue = Over256Kib.ToString(System.Globalization.CultureInfo.InvariantCulture)
        }));
        Ensure(code == "InvalidParameter",
            "raising MaximumMessageSize past 256 KiB with a non-SQS/Firehose/Lambda subscription " +
            $"present must be refused InvalidParameter, got '{code}': '{message}'");
        const string expectedMessage =
            "Invalid parameter: MaximumMessageSize greater than 262144 bytes is not supported for " +
            "the following protocol: [email]";
        Ensure(message == expectedMessage, $"expected the exact message '{expectedMessage}', got '{message}'");
        var still = (await sns.GetTopicAttributesAsync(new GetTopicAttributesRequest { TopicArn = ineligibleArn })).Attributes ?? [];
        Ensure(!still.ContainsKey("MaximumMessageSize"),
            $"the rejected write must leave MaximumMessageSize unset, got '{still.GetValueOrDefault("MaximumMessageSize")}'");

        var eligibleArn = (await sns.CreateTopicAsync(new CreateTopicRequest
        {
            Name = FidelityContext.UniqueName("sim2real-behav-sns-maxmsg-elig-"),
            Tags = [new Tag { Key = "sim2real", Value = Check }],
        })).TopicArn;
        ctx.OnCleanup(() => sns.DeleteTopicAsync(new DeleteTopicRequest { TopicArn = eligibleArn }));
        var queueArn = $"arn:aws:sqs:{ctx.Region}:{ctx.Account}:{FidelityContext.UniqueName("sim2real-behav-sns-maxmsg-q-")}";
        var subArn = (await sns.SubscribeAsync(new SubscribeRequest { TopicArn = eligibleArn, Protocol = "sqs", Endpoint = queueArn })).SubscriptionArn;
        ctx.OnCleanup(() => sns.UnsubscribeAsync(new UnsubscribeRequest { SubscriptionArn = subArn }));

        await sns.SetTopicAttributesAsync(new SetTopicAttributesRequest
        {
            TopicArn = eligibleArn, AttributeName = "MaximumMessageSize", AttributeValue = Over256Kib.ToString(System.Globalization.CultureInfo.InvariantCulture)
        });
        var got = (await sns.GetTopicAttributesAsync(new GetTopicAttributesRequest { TopicArn = eligibleArn })).Attributes ?? [];
        Ensure(got.GetValueOrDefault("MaximumMessageSize") == Over256Kib.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "the identical value must be accepted once every subscription is SQS; expected " +
            $"{Over256Kib}, got '{got.GetValueOrDefault("MaximumMessageSize")}'");
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
