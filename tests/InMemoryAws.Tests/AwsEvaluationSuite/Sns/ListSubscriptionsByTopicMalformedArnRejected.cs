// Translated from behavioral/sns/check_list_subscriptions_by_topic_malformed_arn_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit abd0811).
using Amazon.SimpleNotificationService.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.Sns;

/// <summary>sns.list-subscriptions-by-topic-malformed-arn-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class ListSubscriptionsByTopicMalformedArnRejected
{
    /// <summary>
    /// ListSubscriptionsByTopic with a TopicArn that is not ARN-shaped at all is rejected with
    /// InvalidParameter, distinct from the NotFound a well-formed but deleted topic ARN draws, and
    /// the real topic's listing is unaffected.
    /// </summary>
    [Test]
    [DisplayName("sns.list-subscriptions-by-topic-malformed-arn-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sns = ctx.Sns;
        var topicArn = (await sns.CreateTopicAsync(FidelityContext.UniqueName("sim2real-behav-sns-lsbtarn-"))).TopicArn;
        ctx.OnCleanup(() => sns.DeleteTopicAsync(topicArn));

        // Positive control: the real topic's ARN lists fine.
        var before = Listed(await sns.ListSubscriptionsByTopicAsync(topicArn));
        Ensure(before.Count == 0, $"expected an empty subscription list on a fresh topic, got {Show(before)}");

        // Break: the identifier is well-typed but has none of an ARN's colon-separated structure,
        // so SNS cannot even parse it far enough to look the topic up.
        var (code, message) = await Error(() => sns.ListSubscriptionsByTopicAsync("not-an-arn"));
        Ensure(code == "InvalidParameter", $"expected InvalidParameter for a non-ARN TopicArn, got {Py.Repr(code)}");
        Ensure(message == "Invalid parameter: TopicArn Reason: An ARN must have at least 6 elements, not 1",
            $"unexpected message for a non-ARN TopicArn: {Py.Repr(message)}");

        // The real topic's listing is unaffected by the rejected call.
        var after = Listed(await sns.ListSubscriptionsByTopicAsync(topicArn));
        Ensure(after.Count == 0, $"expected the listing unchanged after the rejected call, got {Show(after)}");
    }

    /// <summary>
    /// The listed subscriptions. The .NET SDK may leave an empty list null where boto3 returns
    /// <c>[]</c>, so both read as empty.
    /// </summary>
    private static List<Subscription> Listed(ListSubscriptionsByTopicResponse response) => response.Subscriptions ?? [];

    private static string Show(List<Subscription> subscriptions) =>
        "[" + string.Join(", ", subscriptions.Select(s => Py.Repr(s.SubscriptionArn))) + "]";

    /// <summary>(Code, Message) of the ClientError fn() raises; an unexpected success raises.</summary>
    private static async Task<(string? Code, string? Message)> Error<T>(Func<Task<T>> fn)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        return error is null
            ? throw Fail($"expected the call to be rejected, but it SUCCEEDED and returned {result}")
            : (error.Code, error.Message);
    }
}
