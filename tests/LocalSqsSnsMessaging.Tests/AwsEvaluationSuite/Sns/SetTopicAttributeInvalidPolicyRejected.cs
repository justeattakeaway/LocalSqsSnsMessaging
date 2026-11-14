// Translated from behavioral/sns/check_set_topic_attribute_invalid_policy_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Text.Json.Nodes;
using Amazon.SimpleNotificationService.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sns;

/// <summary>sns.set-topic-attribute-invalid-policy-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class SetTopicAttributeInvalidPolicyRejected
{
    /// <summary>
    /// SetTopicAttributes rejects a malformed Policy document and leaves the existing policy
    /// untouched; a well-formed Policy update on the same topic then succeeds and round-trips.
    /// </summary>
    [Test]
    [DisplayName("sns.set-topic-attribute-invalid-policy-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sns = ctx.Sns;
        var topicArn = (await sns.CreateTopicAsync(new CreateTopicRequest
        {
            Name = FidelityContext.UniqueName("sim2real-badpolicy-topic-"),
            Tags = [new() { Key = "sim2real", Value = "behavioral" }],
        })).TopicArn;
        ctx.OnCleanup(() => sns.DeleteTopicAsync(new DeleteTopicRequest { TopicArn = topicArn }));

        var originalPolicy = (await sns.GetTopicAttributesAsync(new GetTopicAttributesRequest { TopicArn = topicArn })).Attributes["Policy"];

        var (code, message) = await Error(
            () => sns.SetTopicAttributesAsync(new SetTopicAttributesRequest
            {
                TopicArn = topicArn,
                AttributeName = "Policy",
                AttributeValue = "{this is not valid json",
            }));
        Ensure(code == "InvalidParameter", $"expected InvalidParameter for a malformed Policy document, got '{code}'");
        Ensure(message == "Invalid parameter: Policy Error: null",
            $"expected the exact message 'Invalid parameter: Policy Error: null', got '{message}'");

        var unchangedPolicy = (await sns.GetTopicAttributesAsync(new GetTopicAttributesRequest { TopicArn = topicArn })).Attributes["Policy"];
        Ensure(unchangedPolicy == originalPolicy, "topic policy mutated despite rejected SetTopicAttributes call");

        // positive control: a well-formed Policy update on the SAME attribute still succeeds and round-trips,
        // isolating the malformed-document rejection above rather than a blanket refusal to update Policy at all
        var account = ctx.Account;
        var goodPolicy = new JsonObject
        {
            ["Version"] = "2008-10-17",
            ["Id"] = "sim2real-badpolicy-policy",
            ["Statement"] = new JsonArray(new JsonObject
            {
                ["Sid"] = "AllowSameAccountPublish",
                ["Effect"] = "Allow",
                ["Principal"] = new JsonObject { ["AWS"] = $"arn:aws:iam::{account}:root" },
                ["Action"] = "SNS:Publish",
                ["Resource"] = topicArn,
            }),
        }.ToJsonString();
        await sns.SetTopicAttributesAsync(new SetTopicAttributesRequest { TopicArn = topicArn, AttributeName = "Policy", AttributeValue = goodPolicy });
        var fetchedPolicy = (await sns.GetTopicAttributesAsync(new GetTopicAttributesRequest { TopicArn = topicArn })).Attributes["Policy"];
        Ensure(JsonNode.DeepEquals(JsonNode.Parse(fetchedPolicy), JsonNode.Parse(goodPolicy)),
            $"well-formed Policy update did not round-trip: expected {goodPolicy}, got {fetchedPolicy}");
    }

    private static async Task<(string? Code, string Message)> Error(Func<Task<SetTopicAttributesResponse>> fn)
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is null)
        {
            throw Fail($"expected SetTopicAttributes to be rejected, but it SUCCEEDED and returned {result}");
        }

        return (error.Code, error.Message ?? "");
    }
}
