// Translated from behavioral/sns/check_list_topics_and_subscriptions_forged_next_token_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.SimpleNotificationService.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sns;

/// <summary>sns.list-topics-and-subscriptions-forged-next-token-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class ListTopicsAndSubscriptionsForgedNextTokenRejected
{
    private const string Check = "list-topics-and-subscriptions-forged-next-token-rejected";

    private const string Forged = "c2ltMnJlYWwtbm90LWEtcmVhbC10b2tlbg==";

    /// <summary>
    /// ListTopics and account-wide ListSubscriptions both refuse a NextToken SNS never
    /// issued with InvalidParameter, while the same calls without a token, and with a token
    /// SNS itself issued, keep working.
    /// </summary>
    [Test]
    [DisplayName("sns.list-topics-and-subscriptions-forged-next-token-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    [KnownFidelityGap(FidelityGap.SnsListing, "ListTopics accepts a NextToken SNS never issued and returns a page instead of refusing it with 'Invalid parameter: NextToken'")]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sns = ctx.Sns;
        var topicArn = (await sns.CreateTopicAsync(new CreateTopicRequest
        {
            Name = FidelityContext.UniqueName("sim2real-behav-sns-forgetok-"),
            Tags = [new() { Key = "sim2real", Value = Check }],
        })).TopicArn;
        ctx.OnCleanup(() => sns.DeleteTopicAsync(new DeleteTopicRequest { TopicArn = topicArn }));

        var (code, message) = await Error(() => sns.ListTopicsAsync(new ListTopicsRequest { NextToken = Forged }));
        Ensure(code == "InvalidParameter", $"ListTopics with a forged NextToken gave '{code}'");
        Ensure(message == "Invalid parameter: NextToken", $"unexpected ListTopics forged-token message: '{message}'");

        (code, message) = await Error(() => sns.ListSubscriptionsAsync(new ListSubscriptionsRequest { NextToken = Forged }));
        Ensure(code == "InvalidParameter", $"ListSubscriptions with a forged NextToken gave '{code}'");
        Ensure(message == "Invalid parameter: NextToken", $"unexpected ListSubscriptions forged-token message: '{message}'");

        // Positive controls: the same operations without a token work, and the SNS-driven
        // paginator walk (tokens SNS itself issued) reaches the topic and subscription made here.
        var subArn = (await sns.SubscribeAsync(new SubscribeRequest
        {
            TopicArn = topicArn, Protocol = "sqs", ReturnSubscriptionArn = true,
            Endpoint = string.Join(":", new[] { "arn:aws:sqs" }.Concat(topicArn.Split(':')[3..5]).Append(FidelityContext.UniqueName("sim2real-behav-sns-forgetok-q-"))),
        })).SubscriptionArn;
        ctx.OnCleanup(() => sns.UnsubscribeAsync(new UnsubscribeRequest { SubscriptionArn = subArn }));
        var topics = await Poll(
            async () =>
            {
                var arns = new HashSet<string>();
                await foreach (var p in sns.Paginators.ListTopics(new ListTopicsRequest()).Responses)
                {
                    foreach (var t in p.Topics ?? [])
                    {
                        arns.Add(t.TopicArn);
                    }
                }

                return arns;
            },
            arns => arns.Contains(topicArn), timeoutS: 60.0, intervalS: 3.0,
            what: "the created topic appears in a paginated ListTopics walk");
        Ensure(topics.Contains(topicArn), "the created topic must be listed");
        var subs = await Poll(
            async () =>
            {
                var arns = new HashSet<string>();
                await foreach (var p in sns.Paginators.ListSubscriptions(new ListSubscriptionsRequest()).Responses)
                {
                    foreach (var s in p.Subscriptions ?? [])
                    {
                        arns.Add(s.SubscriptionArn);
                    }
                }

                return arns;
            },
            arns => arns.Contains(subArn), timeoutS: 60.0, intervalS: 3.0,
            what: "the created subscription appears in a paginated ListSubscriptions walk");
        Ensure(subs.Contains(subArn), "the created subscription must be listed");
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

    /// <summary>Poll fn() until pred(val); raise AssertionError on deadline miss (never a bare sleep).</summary>
    private static async Task<T> Poll<T>(Func<Task<T>> fn, Func<T, bool> pred, double timeoutS, double intervalS, string what)
    {
        var deadline = FidelityTime.StartNew();
        var val = await fn();
        while (!pred(val))
        {
            if (deadline.Elapsed >= TimeSpan.FromSeconds(timeoutS))
            {
                throw Fail($"{what}: condition not met within {timeoutS}s");
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(intervalS));
            val = await fn();
        }

        return val;
    }
}
