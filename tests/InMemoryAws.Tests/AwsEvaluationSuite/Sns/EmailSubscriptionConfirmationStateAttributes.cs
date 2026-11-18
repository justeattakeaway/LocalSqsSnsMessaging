// Translated from behavioral/sns/check_email_subscription_confirmation_state_attributes.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.SimpleNotificationService.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.Sns;

/// <summary>sns.email-subscription-confirmation-state-attributes</summary>
[Category("AwsEvaluationSuite")]
public sealed class EmailSubscriptionConfirmationStateAttributes
{
    private const string Check = "email-subscription-confirmation-state-attributes";

    /// <summary>
    /// An unconfirmed email subscription gets a real ARN reading PendingConfirmation=true and
    /// ConfirmationWasAuthenticated=false (an sqs one reads false/true), yet ListSubscriptionsByTopic
    /// still lists it under the PendingConfirmation placeholder.
    /// </summary>
    [Test]
    [DisplayName("sns.email-subscription-confirmation-state-attributes")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sns = ctx.Sns;
        var topicArn = (await sns.CreateTopicAsync(new CreateTopicRequest
        {
            Name = FidelityContext.UniqueName("sim2real-behav-sns-confstate-"),
            Tags = [new Tag { Key = "sim2real", Value = Check }],
        })).TopicArn;
        ctx.OnCleanup(() => sns.DeleteTopicAsync(new DeleteTopicRequest { TopicArn = topicArn }));
        var parts = topicArn.Split(':');
        var (region, account) = (parts[3], parts[4]);

        var sqsSub = (await sns.SubscribeAsync(new SubscribeRequest
        {
            TopicArn = topicArn, Protocol = "sqs", ReturnSubscriptionArn = true,
            Endpoint = $"arn:aws:sqs:{region}:{account}:{FidelityContext.UniqueName("sim2real-behav-sns-confstate-q-")}",
        })).SubscriptionArn;
        ctx.OnCleanup(() => sns.UnsubscribeAsync(new UnsubscribeRequest { SubscriptionArn = sqsSub }));
        // example.com is IANA-reserved; nothing about the email itself is asserted.
        var email = $"{FidelityContext.UniqueName("sim2real-behav-sns")}@example.com";
        var pending = (await sns.SubscribeAsync(new SubscribeRequest
        {
            TopicArn = topicArn, Protocol = "email", Endpoint = email, ReturnSubscriptionArn = true,
        })).SubscriptionArn;
        // A pending subscription cannot be unsubscribed; DeleteTopic (registered above) removes it.
        Ensure(pending.StartsWith(topicArn + ":", StringComparison.Ordinal),
            "with ReturnSubscriptionArn an email subscription must get a real ARN under its topic, " +
            $"not a placeholder; got '{(!pending.StartsWith("arn:", StringComparison.Ordinal) ? pending : "another ARN")}'");

        var p = (await sns.GetSubscriptionAttributesAsync(new GetSubscriptionAttributesRequest { SubscriptionArn = pending })).Attributes ?? [];
        var got = Pick(p, "PendingConfirmation", "ConfirmationWasAuthenticated", "Protocol", "Endpoint");
        Ensure(got == $"PendingConfirmation=true, ConfirmationWasAuthenticated=false, Protocol=email, Endpoint={email}",
            $"pending email subscription attributes: {got}");

        var c = (await sns.GetSubscriptionAttributesAsync(new GetSubscriptionAttributesRequest { SubscriptionArn = sqsSub })).Attributes ?? [];
        got = Pick(c, "PendingConfirmation", "ConfirmationWasAuthenticated");
        Ensure(got == "PendingConfirmation=false, ConfirmationWasAuthenticated=true",
            $"auto-confirmed sqs subscription attributes: {got}");

        var listed = ((await sns.ListSubscriptionsByTopicAsync(new ListSubscriptionsByTopicRequest { TopicArn = topicArn })).Subscriptions ?? [])
            .GroupBy(x => x.SubscriptionArn).ToDictionary(g => g.Key, g => g.Last());
        Ensure(listed.GetValueOrDefault("PendingConfirmation")?.Endpoint == email,
            "ListSubscriptionsByTopic must still list the email subscription under the " +
            "PendingConfirmation placeholder even though Subscribe returned its real ARN");
        Ensure(listed.ContainsKey(sqsSub), "ListSubscriptionsByTopic must list the confirmed sqs subscription by its ARN");
        Ensure(!listed.ContainsKey(pending), "the pending subscription's real ARN must not be listed before confirmation");
    }

    private static string Pick(Dictionary<string, string> attributes, params string[] keys) =>
        string.Join(", ", keys.Select(k => $"{k}={attributes.GetValueOrDefault(k) ?? "None"}"));
}
