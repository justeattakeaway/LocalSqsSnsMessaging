// Translated from behavioral/sns/check_subscription_redrive_policy_round_trip_and_replace.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Text.Json.Nodes;
using Amazon.SimpleNotificationService;
using Amazon.SimpleNotificationService.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sns;

/// <summary>sns.subscription-redrive-policy-round-trip-and-replace</summary>
[Category("AwsEvaluationSuite")]
public sealed class SubscriptionRedrivePolicyRoundTripAndReplace
{
    private const string Check = "subscription-redrive-policy-round-trip-and-replace";

    /// <summary>
    /// A new subscription has no RedrivePolicy; a write stores the dead-letter target, a second
    /// write replaces it, an empty value is refused and leaves that target in place, and none of
    /// this disturbs RawMessageDelivery.
    /// </summary>
    [Test]
    [DisplayName("sns.subscription-redrive-policy-round-trip-and-replace")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sns = ctx.Sns;
        var topicArn = (await sns.CreateTopicAsync(new CreateTopicRequest
        {
            Name = FidelityContext.UniqueName("sim2real-behav-sns-redrive-"),
            Tags = [new() { Key = "sim2real", Value = Check }],
        })).TopicArn;
        ctx.OnCleanup(() => sns.DeleteTopicAsync(new DeleteTopicRequest { TopicArn = topicArn }));
        // SNS stores the dead-letter and endpoint ARNs without contacting the queue, so none exists.
        var subArn = (await sns.SubscribeAsync(new SubscribeRequest
        {
            TopicArn = topicArn,
            Protocol = "sqs",
            ReturnSubscriptionArn = true,
            Endpoint = QueueArn(topicArn, FidelityContext.UniqueName("sim2real-behav-sns-redrive-q-")),
            Attributes = new() { ["RawMessageDelivery"] = "true" },
        })).SubscriptionArn;
        ctx.OnCleanup(() => sns.UnsubscribeAsync(new UnsubscribeRequest { SubscriptionArn = subArn }));

        Ensure(!(await Attrs(sns, subArn)).ContainsKey("RedrivePolicy"), "a new subscription carries no RedrivePolicy");

        var dlq1 = QueueArn(topicArn, FidelityContext.UniqueName("sim2real-behav-sns-dlq1-"));
        await sns.SetSubscriptionAttributesAsync(new SetSubscriptionAttributesRequest
        {
            SubscriptionArn = subArn,
            AttributeName = "RedrivePolicy",
            AttributeValue = $$"""{"deadLetterTargetArn": "{{dlq1}}"}""",
        });
        var got = JsonNode.Parse((await Attrs(sns, subArn))["RedrivePolicy"])!.AsObject();
        Ensure(JsonNode.DeepEquals(got, new JsonObject { ["deadLetterTargetArn"] = dlq1 }),
            $"the first RedrivePolicy read back as keys [{string.Join(", ", got.Select(kv => kv.Key).Order(StringComparer.Ordinal).Select(k => $"'{k}'"))}]");

        var dlq2 = QueueArn(topicArn, FidelityContext.UniqueName("sim2real-behav-sns-dlq2-"));
        await sns.SetSubscriptionAttributesAsync(new SetSubscriptionAttributesRequest
        {
            SubscriptionArn = subArn,
            AttributeName = "RedrivePolicy",
            AttributeValue = $$"""{"deadLetterTargetArn": "{{dlq2}}"}""",
        });
        got = JsonNode.Parse((await Attrs(sns, subArn))["RedrivePolicy"])!.AsObject();
        Ensure(JsonNode.DeepEquals(got, new JsonObject { ["deadLetterTargetArn"] = dlq2 }), "the second write must replace the dead-letter target");

        var (code, message) = await Error(() => sns.SetSubscriptionAttributesAsync(new SetSubscriptionAttributesRequest
        {
            SubscriptionArn = subArn,
            AttributeName = "RedrivePolicy",
            AttributeValue = "",
        }));
        Ensure(code == "InvalidParameter", $"an empty RedrivePolicy gave '{code}'");
        Ensure(message == "Invalid parameter: RedrivePolicy: missing required value", $"unexpected message for an empty RedrivePolicy: '{message}'");
        var final = await Attrs(sns, subArn);
        got = JsonNode.Parse(final["RedrivePolicy"])!.AsObject();
        Ensure(JsonNode.DeepEquals(got, new JsonObject { ["deadLetterTargetArn"] = dlq2 }), "the refused empty write must leave the second target in place");
        Ensure(final.GetValueOrDefault("RawMessageDelivery") == "true",
            $"RedrivePolicy writes must not disturb RawMessageDelivery, got '{final.GetValueOrDefault("RawMessageDelivery")}'");
    }

    private static string QueueArn(string topicArn, string name)
    {
        var parts = topicArn.Split(':');
        return $"arn:aws:sqs:{parts[3]}:{parts[4]}:{name}";
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

    private static async Task<Dictionary<string, string>> Attrs(AmazonSimpleNotificationServiceClient sns, string subArn) =>
        (await sns.GetSubscriptionAttributesAsync(new GetSubscriptionAttributesRequest { SubscriptionArn = subArn })).Attributes ?? [];
}
