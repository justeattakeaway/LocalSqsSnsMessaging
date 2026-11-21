// Translated from behavioral/sns/check_list_subscriptions_by_topic_invalid_next_token_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit abd0811).
using Amazon.SimpleNotificationService.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.Sns;

/// <summary>sns.list-subscriptions-by-topic-invalid-next-token-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class ListSubscriptionsByTopicInvalidNextTokenRejected
{
    /// <summary>
    /// ListSubscriptionsByTopic with a NextToken that was never issued is rejected with
    /// InvalidParameter instead of returning an arbitrary page, and an ordinary call on the same
    /// topic still works before and after the rejected one.
    /// </summary>
    [Test]
    [DisplayName("sns.list-subscriptions-by-topic-invalid-next-token-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sns = ctx.Sns;
        var topicArn = (await sns.CreateTopicAsync(FidelityContext.UniqueName("sim2real-behav-sns-lsbt-"))).TopicArn;
        ctx.OnCleanup(() => sns.DeleteTopicAsync(topicArn));

        // Positive control: an ordinary, token-less call on the real topic succeeds.
        var before = Listed(await sns.ListSubscriptionsByTopicAsync(topicArn));
        Ensure(before.Count == 0, $"expected an empty subscription list on a fresh topic, got {Show(before)}");

        // Break exactly one thing: a NextToken that could never have been issued by SNS.
        var (code, message) = await Error(() => sns.ListSubscriptionsByTopicAsync(new ListSubscriptionsByTopicRequest
        {
            TopicArn = topicArn, NextToken = "not-a-real-token==",
        }));
        Ensure(code == "InvalidParameter", $"expected InvalidParameter for a NextToken that was never issued, got {Py.Repr(code)}");
        Ensure(message == "Invalid parameter: NextToken", $"unexpected message for an unissued NextToken: {Py.Repr(message)}");

        // The rejected call left the topic's own listing unaffected.
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
