// Translated from behavioral/sns/check_op_confirm_subscription_invalid_token_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.SimpleNotificationService;
using Amazon.SimpleNotificationService.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sns;

/// <summary>sns.op-confirm-subscription-invalid-token-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class OpConfirmSubscriptionInvalidTokenRejected
{
    private const string Check = "op-confirm-subscription-invalid-token-rejected";

    /// <summary>ConfirmSubscription must reject a token that was never issued and create no subscription.</summary>
    [Test]
    [DisplayName("sns.op-confirm-subscription-invalid-token-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sns = ctx.Sns;
        var topicArn = await CreateTopic(sns, ctx, FidelityContext.UniqueName("sim2real-behav-sns-confirm-"), tags: TagsKv());

        var (code, message) = await Error(() => sns.ConfirmSubscriptionAsync(new ConfirmSubscriptionRequest
        {
            TopicArn = topicArn, Token = new string('a', 64),
        }));
        Ensure(code == "InvalidParameter", $"expected InvalidParameter for a bogus token, got {code}");
        const string expected = "Invalid parameter: Token";
        Ensure(message == expected, $"expected message '{expected}', got '{message}'");

        var subs = (await sns.ListSubscriptionsByTopicAsync(new ListSubscriptionsByTopicRequest { TopicArn = topicArn })).Subscriptions ?? [];
        Ensure(subs.Count == 0,
            $"a rejected ConfirmSubscription must not create a subscription, got [{string.Join(", ", subs.Select(s => s.SubscriptionArn))}]");
    }

    /// <summary>
    /// Call fn(); return (code, message) of the ClientError it raises. An unexpected success
    /// raises, so a target that accepts what AWS refuses never reads as a code mismatch.
    /// </summary>
    private static async Task<(string? Code, string? Message)> Error<T>(Func<Task<T>> fn)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is null)
        {
            throw Fail($"expected the call to be refused, but it succeeded and returned {result}");
        }

        return (error.Code, error.Message);
    }

    /// <summary>CreateTopic + register best-effort DeleteTopic cleanup; returns the TopicArn.</summary>
    private static async Task<string> CreateTopic(AmazonSimpleNotificationServiceClient client, FidelityContext ctx, string name,
        Dictionary<string, string>? attributes = null, List<Tag>? tags = null)
    {
        var request = new CreateTopicRequest { Name = name };
        if (attributes is { Count: > 0 })
        {
            request.Attributes = attributes;
        }

        if (tags is { Count: > 0 })
        {
            request.Tags = tags;
        }

        var arn = (await client.CreateTopicAsync(request)).TopicArn;
        ctx.OnCleanup(() => client.DeleteTopicAsync(new DeleteTopicRequest { TopicArn = arn }));
        return arn;
    }

    private static List<Tag> TagsKv() =>
        [new() { Key = "sim2real", Value = Check }, new() { Key = "sim2real:managed", Value = "true" }];
}
