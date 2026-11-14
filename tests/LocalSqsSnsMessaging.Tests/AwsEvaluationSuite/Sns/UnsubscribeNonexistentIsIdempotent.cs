// Translated from behavioral/sns/check_unsubscribe_nonexistent_is_idempotent.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.SimpleNotificationService.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sns;

/// <summary>sns.unsubscribe-nonexistent-is-idempotent</summary>
[Category("AwsEvaluationSuite")]
public sealed class UnsubscribeNonexistentIsIdempotent
{
    /// <summary>Call fn(); return botocore (Error.Code, Error.Message). An unexpected SUCCESS raises.</summary>
    private static async Task<(string? Code, string? Message)> Error<T>(Func<Task<T>> fn)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is null)
        {
            throw Fail($"expected a ClientError, but the call SUCCEEDED and returned {result}");
        }

        return (error.Code, error.Message);
    }

    /// <summary>Create a topic, register its teardown, and return its ARN.</summary>
    private static async Task<string> CreateTopic(FidelityContext ctx, string? name = null)
    {
        var sns = ctx.Sns;
        name ??= FidelityContext.UniqueName("sim2real-behav-sns-topic-");
        var arn = (await sns.CreateTopicAsync(new CreateTopicRequest { Name = name })).TopicArn;
        ctx.OnCleanup(() => sns.DeleteTopicAsync(new DeleteTopicRequest { TopicArn = arn }));
        return arn;
    }

    /// <summary>Return a well-formed but nonexistent SubscriptionArn for a topic.</summary>
    private static string FakeSubscriptionArn(string topicArn) => $"{topicArn}:{Guid.NewGuid()}";

    /// <summary>
    /// Unsubscribe on a well-formed but nonexistent SubscriptionArn of an existing topic succeeds,
    /// repeatably, and later reads show no subscription was fabricated and the topic is undisturbed.
    /// </summary>
    [Test]
    [DisplayName("sns.unsubscribe-nonexistent-is-idempotent")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    [KnownFidelityGap(FidelityGap.SnsSubscriptions, "Unsubscribe of a well-formed but nonexistent SubscriptionArn fails NotFound 'Subscription not found.' instead of succeeding")]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sns = ctx.Sns;
        var topicArn = await CreateTopic(ctx);
        var fakeSub = FakeSubscriptionArn(topicArn);

        await sns.UnsubscribeAsync(new UnsubscribeRequest { SubscriptionArn = fakeSub });
        await sns.UnsubscribeAsync(new UnsubscribeRequest { SubscriptionArn = fakeSub });

        var (getCode, getMessage) = await Error(() => sns.GetSubscriptionAttributesAsync(new GetSubscriptionAttributesRequest { SubscriptionArn = fakeSub }));
        Ensure(getCode == "NotFound",
            $"GetSubscriptionAttributes on the never-existent ARN should be NotFound, got '{getCode}'");
        Ensure(getMessage == "Subscription does not exist",
            $"unexpected NotFound message for the never-existent ARN: '{getMessage}'");
        var subs = (await sns.ListSubscriptionsByTopicAsync(new ListSubscriptionsByTopicRequest { TopicArn = topicArn })).Subscriptions ?? [];
        Ensure(subs.Count == 0,
            $"Unsubscribe on a bogus ARN must not leave a fabricated subscription record, got [{string.Join(", ", subs.Select(s => $"'{s.SubscriptionArn}'"))}]");
    }
}
