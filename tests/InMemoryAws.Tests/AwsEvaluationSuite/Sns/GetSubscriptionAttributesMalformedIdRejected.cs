// Translated from behavioral/sns/check_get_subscription_attributes_malformed_id_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit abd0811).
using Amazon.SimpleNotificationService.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.Sns;

/// <summary>sns.get-subscription-attributes-malformed-id-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class GetSubscriptionAttributesMalformedIdRejected
{
    /// <summary>
    /// GetSubscriptionAttributes with a SubscriptionArn that is not ARN-shaped at all is rejected
    /// with InvalidParameter, distinct from the NotFound a well-formed but absent ARN draws, and the
    /// real subscription is unaffected.
    /// </summary>
    [Test]
    [DisplayName("sns.get-subscription-attributes-malformed-id-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sns = ctx.Sns;
        var topicArn = (await sns.CreateTopicAsync(FidelityContext.UniqueName("sim2real-behav-sns-getsubattr-"))).TopicArn;
        ctx.OnCleanup(() => sns.DeleteTopicAsync(topicArn));

        var subArn = (await sns.SubscribeAsync(new SubscribeRequest { TopicArn = topicArn, Protocol = "sqs", Endpoint = FakeQueueArn(topicArn) })).SubscriptionArn;
        Ensure(!string.IsNullOrEmpty(subArn) && subArn != "pending confirmation",
            $"expected an auto-confirmed sqs SubscriptionArn, got {Py.Repr(subArn)}");
        ctx.OnCleanup(() => sns.UnsubscribeAsync(subArn));

        // Positive control: the real subscription's attributes read back fine.
        var control = (await sns.GetSubscriptionAttributesAsync(subArn)).Attributes ?? [];
        Ensure(control.GetValueOrDefault("SubscriptionArn") == subArn,
            $"positive control: expected SubscriptionArn {Py.Repr(subArn)} in the attributes, got {Py.Repr(control)}");

        // Break: the identifier is well-typed (a non-empty string) but carries none of an ARN's
        // colon-separated structure, so SNS cannot even parse it into a subscription id.
        var (result, error) = await FidelityContext.Capture(() => sns.GetSubscriptionAttributesAsync("not-an-arn"));
        if (error is null)
        {
            throw Fail($"expected the call to be rejected, but it SUCCEEDED and returned {result}");
        }

        Ensure(error.Code == "InvalidParameter", $"expected InvalidParameter for a non-ARN SubscriptionArn, got {Py.Repr(error.Code)}");
        Ensure(error.Message == "Invalid parameter: SubscriptionArn Reason: An ARN must have at least 6 elements, not 1",
            $"unexpected message for a non-ARN SubscriptionArn: {Py.Repr(error.Message)}");

        // The real subscription survives the rejected call untouched.
        var after = (await sns.GetSubscriptionAttributesAsync(subArn)).Attributes ?? [];
        Ensure(after.GetValueOrDefault("SubscriptionArn") == subArn,
            $"expected the real subscription's attributes unchanged after the rejected call, got {Py.Repr(after)}");
    }

    /// <summary>
    /// A well-formed SQS queue ARN in the topic's account/region; sqs-protocol Subscribe confirms
    /// without checking the queue exists, so no SQS resource is needed.
    /// </summary>
    private static string FakeQueueArn(string topicArn)
    {
        var parts = topicArn.Split(':');
        return $"arn:aws:sqs:{parts[3]}:{parts[4]}:sim2real-behav-sns-q-{Guid.NewGuid().ToString("N")[..8]}";
    }
}
