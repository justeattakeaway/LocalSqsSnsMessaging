// Translated from behavioral/sns/check_subscription_redrive_policy_non_sqs_target_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Text.Json.Nodes;
using Amazon.SimpleNotificationService;
using Amazon.SimpleNotificationService.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sns;

/// <summary>sns.subscription-redrive-policy-non-sqs-target-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class SubscriptionRedrivePolicyNonSqsTargetRejected
{
    private const string Check = "subscription-redrive-policy-non-sqs-target-rejected";

    /// <summary>
    /// SetSubscriptionAttributes refuses a RedrivePolicy whose deadLetterTargetArn names
    /// another SNS topic instead of an SQS queue, and stores nothing; the same policy naming a
    /// queue ARN is then accepted on the same subscription.
    /// </summary>
    [Test]
    [DisplayName("sns.subscription-redrive-policy-non-sqs-target-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    [KnownFidelityGap(FidelityGap.SnsDeliveryFailures, "a RedrivePolicy targeting a topic is refused with 'deadLetterTargetArn must be an SQS queue ARN' instead of 'deadLetterTargetArn must be a SQS queue'")]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sns = ctx.Sns;
        var topicArn = (await sns.CreateTopicAsync(new CreateTopicRequest
        {
            Name = FidelityContext.UniqueName("sim2real-behav-sns-badredrive-"),
            Tags = [new() { Key = "sim2real", Value = Check }],
        })).TopicArn;
        ctx.OnCleanup(() => sns.DeleteTopicAsync(new DeleteTopicRequest { TopicArn = topicArn }));
        var parts = topicArn.Split(':');
        var (region, account) = (parts[3], parts[4]);
        var subArn = (await sns.SubscribeAsync(new SubscribeRequest
        {
            TopicArn = topicArn, Protocol = "sqs", ReturnSubscriptionArn = true,
            Endpoint = $"arn:aws:sqs:{region}:{account}:{FidelityContext.UniqueName("sim2real-behav-sns-badredrive-q-")}",
        })).SubscriptionArn;
        ctx.OnCleanup(() => sns.UnsubscribeAsync(new UnsubscribeRequest { SubscriptionArn = subArn }));

        var (code, message) = await Error(() => sns.SetSubscriptionAttributesAsync(new SetSubscriptionAttributesRequest
        {
            SubscriptionArn = subArn, AttributeName = "RedrivePolicy",
            AttributeValue = new JsonObject { ["deadLetterTargetArn"] = topicArn }.ToJsonString(),
        }));
        Ensure(code == "InvalidParameter", $"a topic ARN as dead-letter target gave '{code}'");
        Ensure(message == "Invalid parameter: RedrivePolicy: deadLetterTargetArn must be a SQS queue", $"unexpected message for a topic ARN as dead-letter target: '{message}'");
        Ensure(!(await Attrs(sns, subArn)).ContainsKey("RedrivePolicy"), "the refused write must store no RedrivePolicy");

        var dlq = $"arn:aws:sqs:{region}:{account}:{FidelityContext.UniqueName("sim2real-behav-sns-badredrive-dlq-")}";
        var want = new JsonObject { ["deadLetterTargetArn"] = dlq };
        await sns.SetSubscriptionAttributesAsync(new SetSubscriptionAttributesRequest
        {
            SubscriptionArn = subArn, AttributeName = "RedrivePolicy", AttributeValue = want.ToJsonString(),
        });
        var stored = JsonNode.Parse((await Attrs(sns, subArn))["RedrivePolicy"]);
        Ensure(JsonNode.DeepEquals(stored, want), "a queue ARN as dead-letter target must be stored");
    }

    private static async Task<Dictionary<string, string>> Attrs(AmazonSimpleNotificationServiceClient sns, string subArn) =>
        (await sns.GetSubscriptionAttributesAsync(new GetSubscriptionAttributesRequest { SubscriptionArn = subArn })).Attributes ?? [];

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
